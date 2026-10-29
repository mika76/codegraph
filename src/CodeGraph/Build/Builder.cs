using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Scan;
using CodeGraph.Text;

namespace CodeGraph.Build;

/// <summary>Result of a build: the graph plus counters for the report.</summary>
public sealed record BuildResult(Graph Graph, Dictionary<string, int> Stats, Dictionary<string, int> Unresolved);

/// <summary>
/// Turns parsed file facts into one cross-repo graph, resolving names to real targets.
/// Steps: files -> projects -> symbols -> routes -> project libraries -> reachability -> references -> rollups.
/// Resolution rule: candidates must be visible (caller's project + referenced projects); prefer the same file, then
/// the same project; matches that stay ambiguous are skipped, never guessed.
/// </summary>
public sealed partial class Builder
{
    private static readonly Dictionary<string, string> Family = new()
    {
        ["vb6"] = "vb", ["vbnet"] = "vb", ["csharp"] = "cs", ["cpp"] = "cpp", ["powershell"] = "ps", ["batch"] = "ps",
        ["javascript"] = "js", ["typescript"] = "js", ["vue"] = "js", ["sql"] = "sql", ["wix"] = "wix",
        ["rust"] = "rust", ["razor"] = "js",
    };
    private static readonly string[] JsExts = ["", ".ts", ".tsx", ".js", ".jsx", ".vue", ".mjs", ".cjs", ".mts", ".d.ts"];
    private static readonly HashSet<string> ShipBinaries = [".dll", ".exe", ".ocx", ".msm", ".msi", ".tlb", ".sys", ".msu", ".cab"];
    public static readonly HashSet<string> TypeKinds =
    [
        "form", "module", "class", "usercontrol", "designer", "propertypage", "userdocument", "interface",
        "struct", "enum", "record", "component", "type_alias", "installer", "feature", "component_group",
        "package_group", "view",
    ];
    public static readonly HashSet<string> RoutineKinds =
        ["sub", "function", "property", "event_handler", "method", "constructor", "api_declare", "operator"];
    /// <summary>Things code uses by value: enum members and constants (relation "uses_value").</summary>
    public static readonly HashSet<string> ValueKinds = ["enum_member", "constant"];
    public static readonly HashSet<string> DbKinds =
        ["db_table", "db_view", "db_proc", "db_function", "db_trigger", "db_type", "db_synonym"];
    private static readonly HashSet<string> SystemDlls =
    [
        "kernel32", "user32", "gdi32", "advapi32", "shell32", "winmm", "ole32", "oleaut32", "comdlg32", "ws2_32",
        "wsock32", "wininet", "shlwapi", "version", "winspool.drv", "psapi", "iphlpapi", "netapi32", "mpr", "rpcrt4",
        "msvbvm60", "crypt32", "dbghelp", "imm32", "comctl32", "setupapi", "winscard", "hid", "urlmon", "secur32",
        "msvcrt", "ntdll", "msimg32", "winhttp", "dwmapi", "uxtheme", "powrprof", "userenv", "wtsapi32", "odbc32",
    ];
    private static readonly HashSet<string> VbBuiltinTypes =
        ["string", "long", "integer", "boolean", "variant", "object", "date", "double", "single", "byte", "currency"];
    private static readonly HashSet<string> RollupRelations =
    [
        "calls", "instantiates", "uses_type", "inherits", "calls_native", "includes", "executes", "reads", "writes",
        "imports", "uses_component", "uses_value",
    ];
    private static readonly string[] MemberExts = [".frm", ".bas", ".cls", ".ctl", ".cs", ".vb", ".cpp", ".c", ".h", ".sql"];


    /// <summary>A file record plus what the builder assigns to it.</summary>
    private sealed class Rec(FileRecord r)
    {
        public string Repo { get; } = r.Repo;
        public string Rel { get; } = r.Rel;
        public string Abs { get; } = r.Abs;
        public FileFacts Facts { get; } = r.Facts;
        public string Id { get; set; } = "";
        public string Area { get; set; } = "";
    }

    /// <summary>One project, possibly merged from several project files (e.g. .vbp and .vbproj of the same output).</summary>
    private sealed class Proj
    {
        public required ProjectInfo Info;
        public required List<string> Files;
        public required string Repo;
        public required string Dir;
        public required string AbsDir;
        public required string Area;
        public readonly List<(string Path, string Base)> Members = [];
        public readonly List<LibRef> Libs = [];
        public readonly List<(string Path, string Base)> ProjectRefs = [];
        public string Display = "";
    }

    private readonly Config _cfg;
    private readonly List<Rec> _records;
    private readonly Graph _g = new();
    private readonly Dictionary<string, int> _stats = [];
    private readonly Dictionary<string, int> _unresolved = [];
    private readonly List<string> _extIgnore;
    /// <summary>Name prefixes that mark a string literal as a stored procedure (for the "not found" report).</summary>
    private readonly string[] _procPrefixes;

    private Dictionary<string, Rec> _byPath = [];
    private Dictionary<string, JsonObject> _tsconfigs = [];
    private readonly Dictionary<string, HashSet<string>> _fileProjects = [];
    private Dictionary<string, List<Rec>> _byBasename = [];

    private readonly Dictionary<string, Proj> _projects = [];
    private readonly Dictionary<string, string> _projByPath = [];
    private readonly Dictionary<string, HashSet<string>> _projByOutput = [];
    private readonly Dictionary<string, List<string>> _projFiles = [];

    private readonly Dictionary<string, Dictionary<string, string>> _fileSyms = [];
    private readonly Dictionary<string, Dictionary<string, List<string>>> _fileRoutines = [];
    private readonly Dictionary<(string?, string), List<string>> _typeIndex = [];
    private readonly Dictionary<(string?, string), Dictionary<string, List<string>>> _members = [];
    private readonly Dictionary<(string?, string), List<string>> _globals = [];
    /// <summary>File id -> name -> enum members / constants declared in it.</summary>
    private readonly Dictionary<string, Dictionary<string, List<string>>> _fileValues = [];
    /// <summary>VB: public enum members and public module constants, usable by bare name across visible projects.</summary>
    private readonly Dictionary<(string?, string), List<string>> _globalValues = [];
    private readonly Dictionary<(string?, string), HashSet<string>> _bases = [];
    private readonly Dictionary<string, string> _vbGlobals = [];
    private readonly Dictionary<string, string> _symFile = [];
    private readonly Dictionary<string, HashSet<string>> _exports = [];
    private readonly Dictionary<string, string> _db = [];
    private readonly Dictionary<string, List<string>> _dbShort = [];

    private readonly Dictionary<string, HashSet<string>> _reach = [];
    private readonly Dictionary<string, HashSet<string>> _allowedCache = [];

    public Builder(Config cfg, IEnumerable<FileRecord> records)
    {
        _cfg = cfg;
        _records = records.Select(r => new Rec(r)).ToList();
        _extIgnore = cfg.ExternalIgnore.Select(p => p.ToLowerInvariant()).ToList();
        _procPrefixes = cfg.ProcPrefixes.Select(p => p.ToLowerInvariant().TrimEnd('_') + "_").ToArray();
    }

    public static BuildResult BuildGraph(Config cfg, IEnumerable<FileRecord> records)
    {
        var b = new Builder(cfg, records);
        var g = b.Run();
        return new BuildResult(g, b._stats, b._unresolved);
    }

    public Graph Run()
    {
        Files();
        Projects();
        Symbols();
        RouteIndex();
        ProjectLibs();
        Reachability();
        Items();
        ResolveRefs();
        MemberOverrides();
        ItemReferences();
        Rollups();
        var stats = new JsonObject();
        foreach (var (k, v) in _stats) stats[k] = v;
        _g.Meta["stats"] = stats;
        var unresolved = new JsonObject();
        var firstSeen = new JsonObject();
        foreach (var (k, v) in _unresolved.OrderByDescending(kv => kv.Value).Take(200)) unresolved[k] = v;
        // every missing procedure is listed in the report, so keep where each was first called
        foreach (var (k, at) in _firstSeen.Where(kv => kv.Key.StartsWith("exec:", StringComparison.Ordinal)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            firstSeen[k] = at;
        _g.Meta["unresolved"] = unresolved;
        _g.Meta["unresolved_at"] = firstSeen;
        return _g;
    }

    private readonly Dictionary<string, string> _firstSeen = [];

    private HashSet<string>? _columnNames;

    /// <summary>Column names of all tables and views in the graph, lower case ("Id int not null" -> "id").</summary>
    private HashSet<string> ColumnNames => _columnNames ??= _g.Nodes
        .Where(n => n.Kind.StartsWith("db_", StringComparison.Ordinal) && n.Data["columns"] is JsonArray)
        .SelectMany(n => ((JsonArray)n.Data["columns"]!).Select(c => (c?.ToString() ?? "").Trim().Split((char[]?)null, 2)[0].Trim('[', ']')))
        .Where(c => c.Length > 0).Select(c => c.ToLowerInvariant()).ToHashSet();

    /// <summary>Count a name that matched nothing and remember where it was first seen ("Repo: path:line").</summary>
    private void Unresolved(string key, string fid, int line)
    {
        Inc(_unresolved, key);
        if (!_firstSeen.ContainsKey(key))
            _firstSeen[key] = $"{NodeStr(fid, "repo")}: {NodeStr(fid, "path")}:{line}";
    }

    // ================================================================== small helpers

    private static void Inc(Dictionary<string, int> d, string key, int by = 1) =>
        d[key] = d.GetValueOrDefault(key) + by;

    private static TV GetOrAdd<TK, TV>(Dictionary<TK, TV> d, TK key) where TK : notnull where TV : new()
    {
        if (!d.TryGetValue(key, out var v)) d[key] = v = new TV();
        return v;
    }

    private static string? Fam(string? lang) => lang is null ? null : Family.GetValueOrDefault(lang);

    private static string? XStr(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>Python truthiness of a JSON value: non-empty string/array/object, true, non-zero number.</summary>
    private static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out string? s) => !string.IsNullOrEmpty(s),
        JsonValue v when v.TryGetValue(out double d) => d != 0,
        _ => true,
    };

    private static IEnumerable<string> StrList(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue(out string? s) ? s : x?.ToString()).OfType<string>() : [];

    private static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());

    private static string LastPart(string name)
    {
        int i = name.LastIndexOf('.');
        return i < 0 ? name : name[(i + 1)..];
    }

    private Node N(string id) => _g.GetNode(id)!;

    private string? NodeStr(string id, string key) => _g.GetNode(id)?.Str(key);

    // ================================================================== files & areas

    private void Files()
    {
        foreach (var repo in _cfg.Repos)
            _g.Node($"repo:{repo.Name}", ("kind", "repo"), ("name", repo.Name), ("path", repo.Path),
                ("branch", GitBranch(repo.Path)), ("commit", Knowledge.KnowledgeLoader.GitCommit(repo.Path)));
        foreach (var r in _records)
        {
            var f = r.Facts;
            r.Id = $"file:{r.Repo}:{r.Rel}";
            r.Area = r.Rel.Contains('/') ? r.Rel.Split('/')[0] : "(root)";
            string lang = f.Lang;
            if (lang == "tsconfig")
            {
                _tsconfigs[Paths.Key(Paths.Dir(r.Abs))] = f.Extra ?? [];
                continue;
            }
            if (lang is "project" or "solution") continue;
            var n = _g.Node(r.Id, ("kind", "file"), ("name", Paths.Base(r.Rel)), ("repo", r.Repo), ("path", r.Rel),
                ("lang", lang), ("loc", f.Loc), ("area", r.Area), ("component", f.Extra?["component"]),
                ("namespace", f.Extra?["namespace"]));
            foreach (var k in new[] { "attrs", "services", "custom_actions", "shortcuts", "script_lang" })
                if (Truthy(f.Extra?[k]))
                    n[k] = f.Extra![k]!.DeepClone();
            _byPath[Paths.Key(r.Abs)] = r;
            Inc(_stats, $"files_{lang}");
        }
        foreach (var r in _records)
            if (r.Facts.Lang is not ("project" or "solution"))
                GetOrAdd(_byBasename, Paths.Base(r.Rel).ToLowerInvariant()).Add(r);
    }

    private string AreaNode(string repo, string area)
    {
        string aid = $"area:{repo}:{area}";
        if (!_g.HasNode(aid))
        {
            _g.Node(aid, ("kind", "area"), ("name", area), ("repo", repo));
            _g.AddEdge($"repo:{repo}", aid, "contains");
        }
        return aid;
    }

    // ================================================================== projects & solutions

    private static ProjectInfo Copy(ProjectInfo p) => new()
    {
        Format = p.Format, Lang = p.Lang, Name = p.Name, Title = p.Title, Type = p.Type, Output = p.Output,
        Assembly = p.Assembly, Version = p.Version, SdkStyle = p.SdkStyle, Error = p.Error,
        Extra = p.Extra?.DeepClone() as JsonObject,
    };

    private static string? Framework(ProjectInfo p) => XStr(p.Extra, "framework");

    private void Projects()
    {
        var mergedKey = new Dictionary<(string, string, string), string>();
        foreach (var r in _records)
        {
            if (r.Facts.Lang != "project" || r.Facts.Project is not { } p) continue;
            string projDir = Paths.Dir(r.Rel);
            var key = (r.Repo, projDir.ToLowerInvariant(), (p.Output ?? p.Name ?? "").ToLowerInvariant());
            if (!mergedKey.TryGetValue(key, out var pid))
            {
                pid = $"proj:{r.Repo}:{r.Rel}";
                mergedKey[key] = pid;
                _projects[pid] = new Proj
                {
                    Info = Copy(p), Files = [r.Rel], Repo = r.Repo, Dir = projDir, AbsDir = Paths.Dir(r.Abs),
                    Area = r.Area,
                };
            }
            var info = _projects[pid];
            if (!info.Files.Contains(r.Rel)) info.Files.Add(r.Rel);
            string baseDir = Paths.Dir(r.Abs);
            info.Members.AddRange(p.Members.Select(m => (m, baseDir)));
            info.Libs.AddRange(p.Libs);
            info.ProjectRefs.AddRange(p.ProjectRefs.Select(m => (m, baseDir)));
            var i = info.Info;
            if (string.IsNullOrEmpty(i.Version)) i.Version = NullIfEmpty(p.Version);
            if (string.IsNullOrEmpty(i.Title)) i.Title = NullIfEmpty(p.Title);
            if (string.IsNullOrEmpty(i.Type)) i.Type = NullIfEmpty(p.Type);
            if (string.IsNullOrEmpty(i.Assembly)) i.Assembly = NullIfEmpty(p.Assembly);
            if (string.IsNullOrEmpty(i.Output)) i.Output = NullIfEmpty(p.Output);
            if (string.IsNullOrEmpty(Framework(i)) && !string.IsNullOrEmpty(Framework(p))) i.X["framework"] = Framework(p);
            _projByPath[Paths.Key(r.Abs)] = pid;
        }

        // display names must be unique (they become note names)
        var names = new Dictionary<string, int>();
        foreach (var info in _projects.Values)
        {
            var p = info.Info;
            info.Display = NullIfEmpty(p.Title) ?? NullIfEmpty(p.Name) ?? Paths.Stem(info.Files[0]);
            Inc(names, info.Display.ToLowerInvariant());
        }
        foreach (var info in _projects.Values)
            if (names[info.Display.ToLowerInvariant()] > 1)
                info.Display = $"{info.Display} ({(info.Dir.Length > 0 ? info.Dir : info.Repo)})";

        foreach (var (pid, info) in _projects)
        {
            var p = info.Info;
            string aid = AreaNode(info.Repo, info.Area);
            _g.Node(pid, ("kind", "project"), ("name", info.Display), ("repo", info.Repo), ("path", info.Files[0]),
                ("project_files", Arr(info.Files)), ("format", p.Format), ("lang", p.Lang), ("ptype", p.Type),
                ("output", p.Output), ("version", p.Version), ("framework", Framework(p)), ("assembly", p.Assembly),
                ("area", info.Area), ("internal_name", p.Name));
            if (info.Libs.Any(l => l.Kind is "assembly" or "package" && TestFrameworks.Contains(l.Name.Split(',')[0].Trim())))
                _g.Node(pid, ("test", true));
            _g.AddEdge($"repo:{info.Repo}", pid, "contains");
            _g.AddEdge(aid, pid, "contains");
            foreach (var key in new HashSet<string> { Paths.Stem(p.Output), (p.Assembly ?? "").ToLowerInvariant(),
                         (p.Name ?? "").ToLowerInvariant(), info.Display.ToLowerInvariant(), (p.Title ?? "").ToLowerInvariant() })
                if (key.Length > 0)
                    GetOrAdd(_projByOutput, key).Add(pid);
        }

        // members
        foreach (var (pid, info) in _projects)
        {
            var p = info.Info;
            var missing = new List<string>();
            var seen = new HashSet<string>();
            foreach (var (member, baseDir) in info.Members)
            {
                if (!_byPath.TryGetValue(Paths.Key(Paths.Join(baseDir, member)), out var target))
                {
                    if (MemberExts.Contains(Paths.Ext(member))) missing.Add(member);
                    continue;
                }
                if (!seen.Add(target.Id)) continue;
                GetOrAdd(_fileProjects, target.Id).Add(pid);
                _g.AddEdge(pid, target.Id, "contains");
            }
            if (p.SdkStyle || p.Format == "sqlproj" && info.Members.Count == 0)
            {
                // SDK-style: every source file below the project folder (unless another project owns it)
                string prefix = Paths.Key(info.AbsDir) + "/";
                string? want = p.Lang switch
                {
                    "csharp" => "cs", "vbnet" => "vb", "sql" => "sql", "fsharp" => "fs", "javascript" => "js",
                    "wix" => "wix", _ => null,
                };
                foreach (var (k, rec) in _byPath)
                    if (k.StartsWith(prefix, StringComparison.Ordinal) && !seen.Contains(rec.Id) && Fam(rec.Facts.Lang) == want)
                    {
                        GetOrAdd(_fileProjects, rec.Id).Add(pid);
                        _g.AddEdge(pid, rec.Id, "contains");
                    }
            }
            if (missing.Count > 0)
            {
                N(pid)["missing_members"] = Arr(missing.Take(200));
                Inc(_stats, "project_members_missing", missing.Count);
            }
        }
        // SDK-style nested projects: a file belongs to the deepest SDK project only
        foreach (var (fid, pids) in _fileProjects)
        {
            var sdk = pids.Where(p => _projects[p].Info.SdkStyle).ToList();
            if (sdk.Count <= 1) continue;
            string deepest = sdk.MaxBy(p => _projects[p].AbsDir.Length)!;
            foreach (var p in sdk)
                if (p != deepest)
                {
                    pids.Remove(p);
                    if (_g.GetEdge(p, fid, "contains") is { } e) _g.RemoveEdge(e);
                }
        }

        foreach (var (fid, pids) in _fileProjects)
        {
            N(fid)["projects"] = Arr(pids.Order(StringComparer.Ordinal));
            foreach (var p in pids) GetOrAdd(_projFiles, p).Add(fid);
        }
        foreach (var r in _records)
        {
            if (_g.GetNode(r.Id) is not { Kind: "file" } fileNode) continue;
            string aid = AreaNode(r.Repo, r.Area);
            if (_fileProjects.GetValueOrDefault(r.Id) is { Count: > 0 }) continue;
            _g.AddEdge(aid, r.Id, "contains");
            if (r.Facts.Lang is "vb6" or "csharp" or "vbnet")
            {
                fileNode["orphan"] = true;
                Inc(_stats, "orphan_files");
            }
        }

        // project -> project references (ProjectReference / *\A)
        foreach (var (pid, info) in _projects)
            foreach (var (refPath, baseDir) in info.ProjectRefs)
            {
                if (_projByPath.TryGetValue(Paths.Key(Paths.Join(baseDir, refPath)), out var tgt))
                    _g.AddEdge(pid, tgt, "depends_on", attrs: ("via", "project reference"));
                else
                    Inc(_unresolved, "project_ref:" + refPath);
            }

        // solutions / project groups
        foreach (var r in _records)
        {
            if (r.Facts.Lang != "solution" || r.Facts.Solution is not { } s) continue;
            string sid = $"sln:{r.Repo}:{r.Rel}";
            _g.Node(sid, ("kind", "solution"), ("name", Paths.Base(r.Rel)), ("repo", r.Repo), ("path", r.Rel), ("area", r.Area));
            _g.AddEdge($"repo:{r.Repo}", sid, "contains");
            foreach (var rel in s.Projects)
                if (_projByPath.TryGetValue(Paths.Key(Paths.Join(Paths.Dir(r.Abs), rel)), out var tgt))
                    _g.AddEdge(sid, tgt, "contains");
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ================================================================== symbols

    private void Symbols()
    {
        var dbDefs = new Dictionary<string, List<(int Score, Rec R, Symbol S)>>();
        string[] symAttrs = ["namespace", "controls", "control_types", "overloads", "exported", "bases", "value"];

        foreach (var r in _records)
        {
            var f = r.Facts;
            string lang = f.Lang;
            string? fam = Fam(lang);
            string fid = r.Id;
            if (lang is "project" or "solution" || !_g.HasNode(fid)) continue;
            if (Truthy(f.Extra?["exports"]))
                foreach (var pid in _fileProjects.GetValueOrDefault(fid) ?? [])
                    GetOrAdd(_exports, pid).UnionWith(StrList(f.Extra!["exports"]).Select(x => x.ToLowerInvariant()));
            var local = new Dictionary<string, string>();
            foreach (var s in f.Symbols)
            {
                if (DbKinds.Contains(s.Kind))
                {
                    string key = XStr(s.Extra, "key") ?? SqlText.ObjectKey(s.Name);
                    int score = 0;
                    if (LastPart(Paths.Stem(r.Rel)) == LastPart(key)) score += 2;
                    if ((XStr(s.Extra, "verb") ?? "").StartsWith("create", StringComparison.Ordinal)) score += 1;
                    GetOrAdd(dbDefs, key).Add((score, r, s));
                    continue;
                }
                string qual = !string.IsNullOrEmpty(s.Parent) ? $"{s.Parent}.{s.Name}" : s.Name;
                string sid = $"sym:{r.Repo}:{r.Rel}#{qual}";
                var node = _g.Node(sid, ("kind", s.Kind), ("name", s.Name), ("qual", qual), ("file", fid), ("repo", r.Repo),
                    ("lang", lang), ("line", s.Line), ("lines", s.Lines == 0 ? null : s.Lines), ("visibility", s.Visibility),
                    ("signature", s.Signature));
                foreach (var k in symAttrs)
                    if (s.Extra?[k] is { } v) node[k] = v.DeepClone();
                _symFile[sid] = fid;
                local[qual.ToLowerInvariant()] = sid;
                string parentId = !string.IsNullOrEmpty(s.Parent) ? $"sym:{r.Repo}:{r.Rel}#{s.Parent}" : fid;
                if (!string.IsNullOrEmpty(s.Parent) && !_g.HasNode(parentId)) parentId = fid;
                _g.AddEdge(parentId, sid, "contains");
                string nameLow = s.Name.ToLowerInvariant();
                if (TypeKinds.Contains(s.Kind))
                {
                    GetOrAdd(_typeIndex, (fam, nameLow)).Add(sid);
                    string shortName = LastPart(s.Name).ToLowerInvariant();
                    if (shortName != nameLow) GetOrAdd(_typeIndex, (fam, shortName)).Add(sid);
                    foreach (var b in StrList(s.Extra?["bases"]))
                        GetOrAdd(_bases, (fam, shortName)).Add(LastPart(b).ToLowerInvariant());
                }
                else if (RoutineKinds.Contains(s.Kind))
                {
                    GetOrAdd(GetOrAdd(_fileRoutines, fid), nameLow).Add(sid);
                    if (!string.IsNullOrEmpty(s.Parent))
                    {
                        string tkey = LastPart(s.Parent).ToLowerInvariant();
                        GetOrAdd(GetOrAdd(_members, (fam, tkey)), nameLow).Add(sid);
                        if (s.Parent.ToLowerInvariant() != tkey)
                            GetOrAdd(GetOrAdd(_members, (fam, s.Parent.ToLowerInvariant())), nameLow).Add(sid);
                    }
                    bool isModule = Truthy(N(fid)["component"]) && lang == "vb6" && r.Rel.EndsWith(".bas", StringComparison.OrdinalIgnoreCase);
                    bool isPublic = (s.Visibility ?? "public") is "public" or "global" or "friend";
                    if (fam == "vb" && isModule && isPublic || fam is "cpp" or "ps" or "js" && string.IsNullOrEmpty(s.Parent))
                        GetOrAdd(_globals, (fam, nameLow)).Add(sid);
                }
                else if (ValueKinds.Contains(s.Kind))
                {
                    // enum members and constants: Enum.Member / Module.Const like routines, bare names in VB
                    if (!string.IsNullOrEmpty(s.Parent))
                    {
                        string tkey = LastPart(s.Parent).ToLowerInvariant();
                        GetOrAdd(GetOrAdd(_members, (fam, tkey)), nameLow).Add(sid);
                        if (s.Parent.ToLowerInvariant() != tkey)
                            GetOrAdd(GetOrAdd(_members, (fam, s.Parent.ToLowerInvariant())), nameLow).Add(sid);
                    }
                    GetOrAdd(GetOrAdd(_fileValues, fid), nameLow).Add(sid);
                    bool isPublic = (s.Visibility ?? "public") is "public" or "global" or "friend";
                    bool vbGlobal = lang == "vb6" && isPublic &&
                                    (s.Kind == "enum_member" || r.Rel.EndsWith(".bas", StringComparison.OrdinalIgnoreCase));
                    if (vbGlobal) GetOrAdd(_globalValues, (fam, nameLow)).Add(sid);
                }
            }
            _fileSyms[fid] = local;
            if (f.Extra?["globals"] is JsonArray globals)
                foreach (var gv in globals.OfType<JsonObject>())
                    if (XStr(gv, "name") is { } gname && XStr(gv, "type") is { } gtype)
                        _vbGlobals.TryAdd(gname.ToLowerInvariant(), gtype);
        }

        // db objects: one node per object, canonical definition = best scoring file
        foreach (var (key, defs) in dbDefs)
        {
            defs.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : string.CompareOrdinal(a.R.Rel, b.R.Rel));
            var (_, r, s) = defs[0];
            string nid = $"db:{key}";
            var also = defs.Skip(1).Select(d => d.R.Id).Take(50).ToList();
            _g.Node(nid, ("kind", s.Kind), ("name", s.Name), ("file", r.Id), ("repo", r.Repo), ("line", s.Line),
                ("lines", s.Lines == 0 ? null : s.Lines), ("columns", s.Extra?["columns"]),
                ("also_defined_in", also.Count > 0 ? Arr(also) : null),
                ("database", _cfg.Repos.FirstOrDefault(x => x.Name == r.Repo)?.DatabaseOf(r.Rel)));
            _g.AddEdge(r.Id, nid, "defines");
            foreach (var d in defs.Skip(1)) _g.AddEdge(d.R.Id, nid, "alters");
            _db[key] = nid;
            GetOrAdd(_dbShort, LastPart(key)).Add(key);
            Inc(_stats, s.Kind);
        }
    }

    // ================================================================== resolution helpers

    /// <summary>Projects a project can see: itself + everything it references (transitively).</summary>
    private void Reachability()
    {
        var deps = new Dictionary<string, HashSet<string>>();
        foreach (var e in _g.Edges)
            if (e.Relation == "depends_on") GetOrAdd(deps, e.Source).Add(e.Target);
        foreach (var pid in _projects.Keys)
        {
            var seen = new HashSet<string> { pid };
            var stack = new Stack<string>([pid]);
            while (stack.Count > 0)
                foreach (var nxt in deps.GetValueOrDefault(stack.Pop()) ?? [])
                    if (seen.Add(nxt)) stack.Push(nxt);
            _reach[pid] = seen;
        }
    }

    private HashSet<string>? Allowed(string callerFile)
    {
        if (_fileProjects.GetValueOrDefault(callerFile) is not { Count: > 0 } pids) return null;
        string key = string.Join('\n', pids.Order(StringComparer.Ordinal));
        if (!_allowedCache.TryGetValue(key, out var allowed))
        {
            allowed = [];
            foreach (var p in pids) allowed.UnionWith(_reach.GetValueOrDefault(p) ?? [p]);
            _allowedCache[key] = allowed;
        }
        return allowed;
    }

    private string CandFile(string cand) => _symFile.GetValueOrDefault(cand) ?? NodeStr(cand, "file") ?? cand;

    private bool Visible(string callerFile, string cand)
    {
        var allowed = Allowed(callerFile);
        if (allowed is null) return true;
        string cf = CandFile(cand);
        if (_fileProjects.GetValueOrDefault(cf) is not { Count: > 0 } tp)
            // target not in any project (loose file): only visible from the same area
            return NodeStr(cf, "area") == NodeStr(callerFile, "area");
        return tp.Overlaps(allowed);
    }

    private int Rank(string callerFile, string cand)
    {
        string cf = CandFile(cand);
        if (cf == callerFile) return 0;
        var a = _fileProjects.GetValueOrDefault(callerFile);
        var b = _fileProjects.GetValueOrDefault(cf);
        if (a is not null && b is not null && a.Overlaps(b)) return 1;
        if (NodeStr(callerFile, "repo") == NodeStr(cf, "repo"))
            return NodeStr(callerFile, "area") == NodeStr(cf, "area") ? 2 : 3;
        return 4;
    }

    private List<string> Pick(string callerFile, IEnumerable<string> candidates)
    {
        var cands = candidates.Distinct().Where(c => Visible(callerFile, c)).ToList();
        if (cands.Count <= 1) return cands;
        var ranked = cands.Select(c => (Rank: Rank(callerFile, c), Id: c))
            .OrderBy(x => x.Rank).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        int best = ranked[0].Rank;
        var chosen = ranked.Where(x => x.Rank == best).Select(x => x.Id).ToList();
        if (best <= 1 || chosen.Count == 1) return chosen;
        Inc(_stats, "ambiguous_refs");
        return [];
    }


    private string? DbLookup(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        string key = SqlText.ObjectKey(raw);
        if (_db.TryGetValue(key, out var hit)) return hit;
        string shortName = LastPart(key);
        var cands = _dbShort.GetValueOrDefault(shortName) ?? [];
        if (cands.Count == 1) return _db[cands[0]];
        if (cands.Any(k => k.Split('.')[0] == "dbo") || _db.ContainsKey(shortName))
            return _db.GetValueOrDefault(shortName);
        return null;
    }

    private List<string> TypeLookup(string? fam, string? name, string callerFile)
    {
        if (string.IsNullOrEmpty(name)) return [];
        return Pick(callerFile, _typeIndex.GetValueOrDefault((fam, LastPart(name).ToLowerInvariant())) ?? []);
    }

    private List<string> MembersOf(string? fam, string typeSid, string name, int depth = 0)
    {
        string tname = LastPart(N(typeSid).Name).ToLowerInvariant();
        var found = _members.GetValueOrDefault((fam, tname))?.GetValueOrDefault(name.ToLowerInvariant())?.ToList() ?? [];
        if (found.Count > 0 || depth > 2) return found;
        foreach (var b in _bases.GetValueOrDefault((fam, tname)) ?? [])
            foreach (var bt in _typeIndex.GetValueOrDefault((fam, b)) ?? [])
            {
                found = MembersOf(fam, bt, name, depth + 1);
                if (found.Count > 0) return found;
            }
        return [];
    }

    private Dictionary<(string?, string), List<string>>? _implementers;

    /// <summary>Methods named <paramref name="member"/> in classes that implement/extend the given interface or class.</summary>
    private List<string> Implementations(string? fam, string typeSid, string member, int limit = 5)
    {
        if (_implementers is null)
        {
            _implementers = [];
            foreach (var ((f, tname), bases) in _bases)
                foreach (var b in bases)
                    GetOrAdd(_implementers, (f, b)).AddRange(_typeIndex.GetValueOrDefault((f, tname)) ?? []);
        }
        string t = LastPart(N(typeSid).Name).ToLowerInvariant();
        var output = new List<string>();
        foreach (var impl in (_implementers.GetValueOrDefault((fam, t)) ?? []).Take(limit * 2))
        {
            string iname = LastPart(N(impl).Name).ToLowerInvariant();
            output.AddRange((_members.GetValueOrDefault((fam, iname))?.GetValueOrDefault(member.ToLowerInvariant()) ?? [])
                .Where(m => _symFile.GetValueOrDefault(m) == _symFile.GetValueOrDefault(impl)));
        }
        return output.Take(limit).ToList();
    }

    /// <summary>
    /// A member of a type links to the member of the same name in its interface (`implements`) or base class
    /// (`overrides`): changing the interface method changes every implementation. Base types are resolved like any
    /// reference (visible, unambiguous); members are matched by name within the base type's file.
    /// </summary>
    private void MemberOverrides()
    {
        foreach (var ((fam, tname), bases) in _bases)
            foreach (var typeSid in _typeIndex.GetValueOrDefault((fam, tname)) ?? [])
            {
                string typeFile = _symFile.GetValueOrDefault(typeSid) ?? "";
                var own = (_members.GetValueOrDefault((fam, tname)) ?? [])
                    .SelectMany(kv => kv.Value.Where(m => _symFile.GetValueOrDefault(m) == typeFile).Select(m => (Name: kv.Key, Sid: m)))
                    .Where(m => N(m.Sid).Kind != "constructor").ToList();
                foreach (var b in bases)
                    foreach (var baseSid in Pick(typeFile, _typeIndex.GetValueOrDefault((fam, b)) ?? []))
                    {
                        if (baseSid == typeSid) continue;
                        string rel = N(baseSid).Kind == "interface" ? "implements" : "overrides";
                        string baseFile = _symFile.GetValueOrDefault(baseSid) ?? "";
                        var baseMembers = _members.GetValueOrDefault((fam, LastPart(N(baseSid).Name).ToLowerInvariant())) ?? [];
                        foreach (var (name, sid) in own)
                            foreach (var bm in baseMembers.GetValueOrDefault(name) ?? [])
                                if (_symFile.GetValueOrDefault(bm) == baseFile && N(bm).Kind != "constructor")
                                    _g.AddEdge(sid, bm, rel);
                    }
            }
    }

    // ================================================================== references

    /// <summary>Table / view node id -> column name (lower case) -> column node id.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _columns = [];

    /// <summary>A node per column of each table / view with known columns: col:&lt;table&gt;.&lt;column&gt;.</summary>
    private void ColumnNodes()
    {
        foreach (var n in _g.Nodes.Where(n => n.Kind is "db_table" or "db_view" && n.Data["columns"] is JsonArray).ToList())
        {
            var cols = new Dictionary<string, string>();
            foreach (var c in (JsonArray)n.Data["columns"]!)
            {
                string def = (c?.ToString() ?? "").Trim();
                string col = def.Split((char[]?)null, 2)[0].Trim('[', ']');
                string key = col.ToLowerInvariant();
                if (col.Length == 0 || cols.ContainsKey(key)) continue;
                string cid = $"col:{n.Id[3..]}.{key}";
                _g.Node(cid, ("kind", "column"), ("name", $"{n.Name}.{col}"), ("table", n.Id), ("definition", def),
                    ("repo", n.Str("repo")), ("file", n.Str("file")), ("line", n.Data["line"]?.DeepClone()),
                    ("database", n.Str("database")));
                _g.AddEdge(n.Id, cid, "contains");
                cols[key] = cid;
            }
            _columns[n.Id] = cols;
        }
    }

    private void ResolveRefs()
    {
        ColumnNodes();
        JsImports();
        ClientRoutes();
        ViewLinks();
        foreach (var r in _records)
        {
            var f = r.Facts;
            string lang = f.Lang;
            string? fam = Fam(lang);
            string fid = r.Id;
            if (lang is "project" or "solution" || !_g.HasNode(fid)) continue;
            var local = _fileSyms.GetValueOrDefault(fid) ?? [];
            foreach (var rf in f.Refs)
            {
                // candidates - any identifier-like string, any x.y( in SQL, a URL given to an unknown function - only
                // count once they link to something
                bool candidate = rf.Kind == "literal" || (rf.Kind == "sql" && rf.Mode == "call") || rf.Extra?["candidate"] is not null;
                // ---- who is calling
                string caller;
                if (XStr(rf.Extra, "item") is { } item && _g.HasNode(item))
                    // SQL embedded in an extractor item: inside a CREATE it's the DB object, otherwise the item
                    caller = (!string.IsNullOrEmpty(rf.From) ? _db.GetValueOrDefault(rf.From) : null) ?? item;
                else if (lang == "sql")
                    caller = (!string.IsNullOrEmpty(rf.From) ? _db.GetValueOrDefault(rf.From) : null) ?? fid;
                else
                {
                    string? c = null;
                    if (!string.IsNullOrEmpty(rf.From))
                        c = local.GetValueOrDefault((!string.IsNullOrEmpty(rf.FromParent) ? $"{rf.FromParent}.{rf.From}" : rf.From).ToLowerInvariant());
                    if (c is null && !string.IsNullOrEmpty(rf.FromParent))
                        c = local.GetValueOrDefault(rf.FromParent.ToLowerInvariant());
                    caller = c ?? fid;
                }
                bool resolved = ResolveOne(fam, fid, caller, rf);
                if (rf.Kind == "column")
                {
                    // column uses are counted on their own: they would swamp the reference rate
                    if (resolved) Inc(_stats, "column_uses");
                    continue;
                }
                if (!candidate || resolved) Inc(_stats, "refs_total");
                if (resolved) Inc(_stats, "refs_resolved");
            }
        }
    }

    private bool EdgeToTargets(string caller, IEnumerable<string> targets, string rel, params (string, JsonNode?)[] attrs)
    {
        bool ok = false;
        foreach (var t in targets)
            if (t != caller && _g.AddEdge(caller, t, ValueRel(t, rel), attrs: attrs) is not null)
                ok = true;
        return ok;
    }

    private bool Edge(string source, string target, string rel, params (string, JsonNode?)[] attrs) =>
        _g.AddEdge(source, target, ValueRel(target, rel), attrs: attrs) is not null;

    /// <summary>A "call" that lands on an enum member or constant is a use of its value.</summary>
    private string ValueRel(string target, string rel) =>
        rel == "calls" && _g.GetNode(target) is { } n && ValueKinds.Contains(n.Kind) ? "uses_value" : rel;

    private bool ResolveOne(string? fam, string fid, string caller, Ref rf)
    {
        string kind = rf.Kind;
        string name = rf.Name ?? "";
        string? callerParent = null;
        if (caller.StartsWith("sym:", StringComparison.Ordinal))
        {
            string q = NodeStr(caller, "qual") ?? "";
            int dot = q.LastIndexOf('.');
            callerParent = dot < 0 ? q : q[..dot];
        }
        int? line = rf.Line == 0 ? null : rf.Line;
        var lineAttr = ("line", (JsonNode?)line);

        if (fam == "js" && ResolveJs(fid, caller, rf) is { } done) return done;
        switch (kind)
        {
            case "ships":
                return ResolveShips(fid, caller, rf);
            case "endpoint":
            {
                string? verb = XStr(rf.Extra, "verb");
                // Url.Action(action, controller) names an action of the same web app: by name first, routes as the
                // fallback, both limited to what the view's project can see (never another repo's API)
                bool conventional = rf.Extra?["conventional"] is not null;
                var hits = conventional ? Pick(fid, _actions.GetValueOrDefault(name) ?? []) : [];
                if (hits.Count == 0) hits = MatchRoute(verb, name);
                if (conventional) hits = hits.Where(h => Visible(fid, h) && Rank(fid, h) <= 3).ToList(); // same repo
                if (hits.Count == 0)
                {
                    // a URL passed to an unknown function is only a guess: not an "unmatched API call"
                    if (rf.Extra?["candidate"] is null)
                        Inc(_unresolved, "api:" + ((verb ?? "").ToUpperInvariant() + " /" + name).Trim());
                    return false;
                }
                // a link the user follows only leads to the page: links_to, which impact does not follow
                string apiRel = rf.Extra?["navigation"] is not null ? "links_to" : "calls_api";
                return EdgeToTargets(caller, hits, apiRel, lineAttr, ("via", XStr(rf.Extra, "raw")));
            }
            case "cs_type":
                return EdgeToTargets(caller, Pick(fid, _typeIndex.GetValueOrDefault(("cs", LastPart(name).ToLowerInvariant())) ?? []),
                    "uses_type", lineAttr);
            case "partial":
            {
                var cands = (_byBasename.GetValueOrDefault(name.ToLowerInvariant() + ".cshtml") ?? []).Select(c => c.Id);
                return EdgeToTargets(caller, Pick(fid, cands), "renders", lineAttr);
            }
            case "wix_ref":
                return EdgeToTargets(caller, Pick(fid, _typeIndex.GetValueOrDefault(("wix", name.ToLowerInvariant())) ?? []),
                    "includes", lineAttr);
            case "sql":
            {
                string? tgt = DbLookup(name);
                if (tgt is null)
                {
                    // x.y( is often a method (XML .value(), a CLR type), not a missing function: not reported
                    if (rf.Mode != "call") Unresolved((rf.Mode == "exec" ? "exec:" : "db:") + name, fid, rf.Line);
                    return false;
                }
                string tk = N(tgt).Kind;
                string? rel = rf.Mode switch
                {
                    "exec" when tk is "db_proc" or "db_function" => "executes",
                    "call" when tk == "db_function" => "calls",
                    "read" or "write" when tk is "db_table" or "db_view" or "db_synonym" or "db_function" =>
                        rf.Mode == "read" || tk == "db_function" ? "reads" : "writes",
                    "alter" => "alters",
                    _ => null,
                };
                if (rel is null) return false;
                return Edge(caller, tgt, rel, lineAttr, ("dynamic", rf.Extra?["dynamic"]), ("via", rf.Via));
            }
            case "route_link":
            {
                // navigation to a client-side route of the same repo: links_to, which impact does not follow
                string? target = RouteTarget(NodeStr(fid, "repo") ?? "", XStr(rf.Extra, "route_name") is null ? name : null, XStr(rf.Extra, "route_name"));
                return target is not null && Edge(caller, target, "links_to", lineAttr);
            }
            case "column":
            {
                // the qualified table's column, or the one statement table that has such a column - never a guess
                var tableKeys = XStr(rf.Extra, "table") is { } qualified ? [qualified]
                    : (rf.Extra?["tables"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToList() ?? [];
                var tables = tableKeys.Select(DbLookup).OfType<string>().Distinct()
                    .Where(t => _columns.GetValueOrDefault(t)?.ContainsKey(name) == true).ToList();
                if (tables.Count != 1 || tables[0] == caller) return false;
                string rel = rf.Mode switch { "write" => "writes", "alter" => "alters", _ => "reads" };
                return Edge(caller, _columns[tables[0]][name], rel, lineAttr, ("dynamic", rf.Extra?["dynamic"]));
            }
            case "literal":
            {
                // rs.Fields("x") / row["x"] reads a column (DbAccess marks it); "Name_" + x is only the start of a name
                if (rf.Extra?["column"] is not null || name.EndsWith('_')) return false;
                // 'schema.name' must match exactly: "config.save" is no call to some procedure named save
                string? tgt = name.Contains('.') ? _db.GetValueOrDefault(SqlText.ObjectKey(name)) : DbLookup(name);
                // a bare string is a call only next to database access (DbAccess marks it) or with a configured
                // prefix (e.g. a constants module naming procedures); otherwise any word could match a procedure
                string shortName = SqlText.ShortName(name);
                // the prefix alone ("sp_", as in name.StartsWith("sp_")) names nothing
                bool prefixed = _procPrefixes.Any(p => shortName.Length > p.Length && shortName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (tgt is not null && N(tgt).Kind is "db_proc" or "db_function" && (prefixed || rf.Extra?["db"] is not null))
                    return Edge(caller, tgt, "executes", lineAttr, ("via", "name in string"));
                // a missing procedure, unless it is the name of a known column (GetValue(row, "x") in your own helper)
                if (tgt is null && prefixed && !ColumnNames.Contains(shortName))
                    Unresolved("exec:" + name, fid, rf.Line);
                return false;
            }
            case "fk":
            {
                string src = _db.GetValueOrDefault(rf.From ?? "") ?? caller;
                string? tgt = DbLookup(name);
                return tgt is not null && Edge(src, tgt, "references");
            }
            case "trigger_on":
            {
                string? tgt = DbLookup(name);
                return tgt is not null && Edge(caller, tgt, "triggers_on");
            }
            case "new" or "type" or "inherit":
            {
                if (fam == "vb" && VbBuiltinTypes.Contains(name.ToLowerInvariant())) return false;
                var tg = TypeLookup(fam, name, fid);
                if (tg.Count == 0) return false;
                string rel = kind switch { "new" => "instantiates", "type" => "uses_type", _ => "inherits" };
                string src = caller;
                if (kind == "inherit" && !string.IsNullOrEmpty(rf.FromParent))
                    src = _fileSyms.GetValueOrDefault(fid)?.GetValueOrDefault(rf.FromParent.ToLowerInvariant()) ?? caller;
                return EdgeToTargets(src, tg.Where(t => t != src), rel, lineAttr);
            }
            case "id": // VB6 bare identifier
            {
                string low = name.ToLowerInvariant();
                if (_fileRoutines.GetValueOrDefault(fid)?.GetValueOrDefault(low) is { Count: > 0 } same)
                    return EdgeToTargets(caller, same, "calls", lineAttr);
                if (_fileValues.GetValueOrDefault(fid)?.GetValueOrDefault(low) is { Count: > 0 } sameValue)
                    return EdgeToTargets(caller, sameValue, "uses_value", lineAttr);
                var glob = Pick(fid, _globals.GetValueOrDefault((fam, low)) ?? []);
                if (glob.Count > 0) return EdgeToTargets(caller, glob, "calls", lineAttr);
                var globValue = Pick(fid, _globalValues.GetValueOrDefault((fam, low)) ?? []);
                if (globValue.Count > 0) return EdgeToTargets(caller, globValue, "uses_value", lineAttr);
                var tg = Pick(fid, _typeIndex.GetValueOrDefault((fam, low)) ?? []);
                return tg.Count > 0 && EdgeToTargets(caller, tg, "uses_type", lineAttr);
            }
            case "call": // bare call in C#, C/C++, JS, PowerShell
            {
                string low = name.ToLowerInvariant();
                if (!string.IsNullOrEmpty(callerParent) && fam is "cs" or "cpp" or "js")
                    foreach (var t in TypeLookup(fam, callerParent, fid).Take(1))
                    {
                        var m = MembersOf(fam, t, name);
                        if (m.Count > 0)
                        {
                            var picked = Pick(fid, m);
                            return EdgeToTargets(caller, picked.Count > 0 ? picked : m.Take(1), "calls", lineAttr);
                        }
                    }
                if (_fileRoutines.GetValueOrDefault(fid)?.GetValueOrDefault(low) is { Count: > 0 } same)
                    return EdgeToTargets(caller, same, "calls", lineAttr);
                var glob = Pick(fid, _globals.GetValueOrDefault((fam, low)) ?? []);
                if (glob.Count > 0) return EdgeToTargets(caller, glob, "calls", lineAttr);
                if (fam == "cs" && TypeLookup(fam, name, fid) is { Count: > 0 } tg)
                    return EdgeToTargets(caller, tg, "instantiates", lineAttr);
                return false;
            }
            case "member":
            {
                string qual = rf.Qual ?? "";
                string? qtype = rf.QualType;
                if (string.IsNullOrEmpty(qtype) && fam == "vb") qtype = _vbGlobals.GetValueOrDefault(qual.ToLowerInvariant());
                string tname = LastPart(!string.IsNullOrEmpty(qtype) ? qtype : qual);
                var tg = TypeLookup(fam, tname, fid);
                if (tg.Count == 0) return false;
                bool hit = false;
                foreach (var t in tg)
                {
                    var ms = MembersOf(fam, t, name);
                    var sameFile = ms.Where(m => _symFile.GetValueOrDefault(m) == _symFile.GetValueOrDefault(t)).ToList();
                    if (sameFile.Count > 0) ms = sameFile;
                    if (ms.Count > 0)
                    {
                        hit |= EdgeToTargets(caller, ms, "calls", lineAttr);
                        if (N(t).Kind is "interface" or "class" && fam == "cs" && Implementations(fam, t, name) is { Count: > 0 } impl)
                            EdgeToTargets(caller, impl, "calls", lineAttr, ("via", "implementation"));
                    }
                    else if (fam == "vb" && N(t).Kind == "form" && name.Equals("Show", StringComparison.OrdinalIgnoreCase))
                        hit |= Edge(caller, t, "opens", lineAttr); // frmOrder.Show vbModal - the VB form's own method
                    else if (t != caller && !caller.StartsWith(t + ".", StringComparison.Ordinal))
                        hit |= Edge(caller, t, "uses_type", lineAttr);
                }
                return hit;
            }
            case "native":
            {
                string lib = XStr(rf.Extra, "lib") ?? "";
                string libStem = Paths.Stem(lib);
                if (SystemDlls.Contains(libStem)) return false;
                // a native DLL comes from a C/C++ project (a .NET project of the same name is not it)
                foreach (var pid in (_projByOutput.GetValueOrDefault(libStem) ?? []).Where(p => CanProvide("native", _projects.GetValueOrDefault(p)?.Info.Format)))
                {
                    string fn = name.ToLowerInvariant();
                    var cands = new List<string>();
                    foreach (var pf in _projFiles.GetValueOrDefault(pid) ?? [])
                        if (_fileRoutines.GetValueOrDefault(pf)?.GetValueOrDefault(fn) is { } h) cands.AddRange(h);
                    if (cands.Count > 0) return EdgeToTargets(caller, cands, "calls_native", lineAttr, ("lib", lib));
                    return Edge(caller, pid, "calls_native", lineAttr, ("lib", lib), ("function", name));
                }
                string? ext = External(lib, "native");
                return ext is not null && Edge(caller, ext, "calls_native", ("function", name));
            }
            case "include":
                return EdgeToTargets(fid, Pick(fid, (_byBasename.GetValueOrDefault(name.ToLowerInvariant()) ?? []).Select(c => c.Id)), "includes");
            case "mention":
            {
                string low = name.ToLowerInvariant();
                var cands = (_byBasename.GetValueOrDefault(low) ?? []).Select(c => c.Id).ToList();
                // copy: the index set must not be changed (the Python added to it in place)
                var pids = low.EndsWith(".exe") || low.EndsWith(".dll") || low.EndsWith(".ocx")
                    ? new HashSet<string>(_projByOutput.GetValueOrDefault(Paths.Stem(low)) ?? [])
                    : [];
                foreach (var (pk, pid) in _projByPath)
                    if (pk.EndsWith("/" + low, StringComparison.Ordinal)) pids.Add(pid);
                var targets = cands.Count > 0 ? Pick(fid, cands) : [];
                targets.AddRange(pids.Order(StringComparer.Ordinal).Take(3));
                return EdgeToTargets(fid, targets.Take(5), "mentions");
            }
        }
        return false;
    }

    // ================================================================== libraries

    private string? External(string? name, string? kind, string? version = null)
    {
        string low = (name ?? "").Trim().ToLowerInvariant();
        if (low.Length == 0) return null;
        if (_extIgnore.Any(p => Glob.Match(low, p))) return null;
        string eid = $"ext:{low}";
        _g.Node(eid, ("kind", "library"), ("name", name!.Trim()), ("lib_kind", kind), ("version", version));
        return eid;
    }

    /// <summary>.NET test frameworks: a project referencing one is a test project (not shipped).</summary>
    private static readonly HashSet<string> TestFrameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk", "xunit", "xunit.core", "xunit.v3", "nunit", "nunit.framework", "MSTest.TestFramework",
        "MSTest", "Microsoft.VisualStudio.QualityTools.UnitTestFramework", "Microsoft.VisualStudio.TestPlatform.TestFramework",
    };

    private static readonly HashSet<string> DotNetFormats = ["csproj", "vbproj", "fsproj"];
    private static readonly HashSet<string> NativeFormats = ["vcxproj", "vcproj", "dsp"];

    /// <summary>Whether a project of this format can be what a reference of this kind points at.</summary>
    private static bool CanProvide(string libKind, string? format) => libKind switch
    {
        // a database project builds SQL CLR assemblies too
        "assembly" or "package" => format is not null && (DotNetFormats.Contains(format) || format == "sqlproj"),
        "native" => format is not null && NativeFormats.Contains(format),
        "npm" or "npm-dev" => format == "npm",
        // COM components and controls come from VB6, C++ or .NET (COM-visible) projects
        "com" or "ocx" => format is not null && (format == "vbp" || DotNetFormats.Contains(format) || NativeFormats.Contains(format)),
        _ => true,
    };

    /// <summary>.NET Framework / runtime assemblies referenced by name only.</summary>
    private static bool IsFrameworkAssembly(string? name)
    {
        string n = (name ?? "").Split(',')[0].Trim();
        return n is "System" or "mscorlib" or "netstandard" or "WindowsBase" or "PresentationCore" or "PresentationFramework"
                   or "Accessibility" or "UIAutomationClient" or "UIAutomationTypes"
               || n.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
    }

    private void ProjectLibs()
    {
        foreach (var (pid, info) in _projects)
            foreach (var lib in info.Libs)
            {
                var names = new HashSet<string> { Paths.Stem(lib.File), (lib.Name ?? "").ToLowerInvariant(), Paths.Stem(lib.Path) };
                names.Remove("");
                var targets = new HashSet<string>();
                // a framework assembly (<Reference Include="System" />, no HintPath) is never one of your projects
                if (!(lib.Kind == "assembly" && string.IsNullOrEmpty(lib.Path) && IsFrameworkAssembly(lib.Name)))
                    foreach (var n in names) targets.UnionWith(_projByOutput.GetValueOrDefault(n) ?? []);
                targets.Remove(pid);
                // only a project that can produce that kind of library: a .NET assembly is no VB6 exe of that name
                targets.RemoveWhere(t => !CanProvide(lib.Kind, _projects.GetValueOrDefault(t)?.Info.Format));
                if (targets.Count > 0)
                {
                    foreach (var t in targets)
                        _g.AddEdge(pid, t, "depends_on", attrs: [("via", lib.Kind), ("version", lib.Version)]);
                    continue;
                }
                string? label = NullIfEmpty(lib.Name) ?? lib.File;
                if (lib.Kind is "ocx" or "com" && !string.IsNullOrEmpty(lib.File))
                    label = lib.Kind == "ocx" ? lib.File : NullIfEmpty(lib.Name) ?? lib.File;
                if (External(label, lib.Kind, lib.Version) is { } ext)
                    _g.AddEdge(pid, ext, "uses_library", attrs: ("via", lib.Kind));
            }
        // C/C++ .def exports become a node attribute on the project
        foreach (var (pid, names) in _exports)
            if (_g.GetNode(pid) is { } n) n["exports"] = Arr(names.Order(StringComparer.Ordinal));
    }

    // ================================================================== rollups

    /// <summary>Project-level dependencies derived from code calls that cross project boundaries.</summary>
    private void Rollups()
    {
        var nodeFile = new Dictionary<string, string>();
        foreach (var n in _g.Nodes)
        {
            if (n.Kind == "file") nodeFile[n.Id] = n.Id;
            else if (n.Str("file") is { } file) nodeFile[n.Id] = file;
        }
        var cross = new Dictionary<(string, string), int>();
        var shipped = new Dictionary<(string, string), int>();
        foreach (var e in _g.Edges.ToList())
        {
            if (e.Relation == "ships" && NodeStr(e.Target, "kind") is "project" or "library" or "file")
            {
                if (nodeFile.GetValueOrDefault(e.Source) is { } shipFile)
                    foreach (var a in _fileProjects.GetValueOrDefault(shipFile) ?? [])
                        shipped[(a, e.Target)] = shipped.GetValueOrDefault((a, e.Target)) + 1;
                continue;
            }
            if (!RollupRelations.Contains(e.Relation)) continue;
            if (nodeFile.GetValueOrDefault(e.Source) is not { } sf || nodeFile.GetValueOrDefault(e.Target) is not { } tf) continue;
            var sp = _fileProjects.GetValueOrDefault(sf);
            var tp = _fileProjects.GetValueOrDefault(tf);
            if (sp is not { Count: > 0 } || tp is not { Count: > 0 } || sp.Overlaps(tp)) continue;
            foreach (var a in sp)
                foreach (var b in tp)
                    cross[(a, b)] = cross.GetValueOrDefault((a, b)) + e.Count;
        }
        foreach (var ((a, b), c) in cross) _g.AddEdge(a, b, "uses_code_of", count: c);
        foreach (var ((a, b), c) in shipped) _g.AddEdge(a, b, "ships", count: c);
    }

    private static string? GitBranch(string path)
    {
        try
        {
            string head = File.ReadAllText(Path.Combine(path, ".git", "HEAD")).Trim();
            int i = head.IndexOf("refs/heads/", StringComparison.Ordinal);
            return i >= 0 ? head[(i + "refs/heads/".Length)..] : head[..Math.Min(12, head.Length)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
