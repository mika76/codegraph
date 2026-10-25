using System.Text.Json.Nodes;
using CodeGraph.Model;

namespace CodeGraph.Build;

// JS / TS / Vue imports, HTTP routes, MVC views and installer "ships" links.
public sealed partial class Builder
{
    private readonly Dictionary<string, Dictionary<string, (string? File, string Imported)>> _jsImports = [];
    private readonly Dictionary<string, string?> _jsDefault = [];
    private readonly Dictionary<string, (string Abs, JsonObject Reexports, List<string> StarExports)> _jsFacts = [];
    private readonly Dictionary<string, string?> _entryCache = [];
    /// <summary>Segment count -> routes (verb, segments, symbol id).</summary>
    private readonly Dictionary<int, List<(string? Verb, string[] Segs, string Sid)>> _routes = [];
    private readonly Dictionary<string, HashSet<string>> _prefixOwners = [];
    /// <summary>"controller/action" (lower case, without the Controller suffix) -> action symbols.</summary>
    private readonly Dictionary<string, List<string>> _actions = [];
    private HashSet<string> _basePaths = [];

    /// <summary>Resolve import specifiers to files; remember local names for call resolution.</summary>
    private void JsImports()
    {
        foreach (var r in _records)
        {
            var f = r.Facts;
            if (Fam(f.Lang) != "js" || !_g.HasNode(r.Id)) continue;
            string fid = r.Id;
            _jsDefault[fid] = XStr(f.Extra, "default_export");
            _jsFacts[fid] = (r.Abs, f.Extra?["reexports"] as JsonObject ?? [], StrList(f.Extra?["star_exports"]).ToList());
            var local = new Dictionary<string, (string?, string)>();
            foreach (var imp in (f.Extra?["imports"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string spec = XStr(imp, "spec") ?? "";
                int? line = imp["line"] is JsonValue lv && lv.TryGetValue(out int li) ? li : null;
                string? target = ResolveSpec(r.Abs, spec);
                if (target is not null)
                    _g.AddEdge(fid, target, "imports", attrs: [("line", line), ("via", Truthy(imp["lazy"]) ? "lazy" : null)]);
                else if (!spec.StartsWith('.') && !spec.StartsWith('/'))
                {
                    var segs = spec.Split('/');
                    string pkg = spec.StartsWith('@') ? string.Join('/', segs.Take(2)) : segs[0];
                    string? entry = spec == pkg ? PackageEntry(pkg) : null;
                    if (entry is not null)
                    {
                        target = entry;
                        _g.AddEdge(fid, entry, "imports", attrs: [("line", line), ("via", "package " + pkg)]);
                    }
                    else if (pkg.Length > 0 && !pkg.StartsWith('~') && !pkg.StartsWith("@/") && PackageEntry(pkg) is null)
                    {
                        if (External(pkg, "npm") is { } ext) _g.AddEdge(fid, ext, "imports");
                    }
                }
                if (imp["names"] is JsonObject names)
                    foreach (var (localName, imported) in names)
                        local[localName.ToLowerInvariant()] = (target, imported?.ToString() ?? "");
            }
            _jsImports[fid] = local;
        }
    }

    private string? ResolveSpec(string absFile, string spec)
    {
        string baseDir = Paths.Dir(absFile);
        var cands = new List<string>();
        if (spec.StartsWith('.'))
            cands.Add(Paths.Join(baseDir, spec));
        else
        {
            // tsconfig / jsconfig "paths" of the nearest config, then the common "@/" and "~/" -> src convention
            string d = Paths.Key(baseDir);
            while (true)
            {
                if (_tsconfigs.TryGetValue(d, out var cfg))
                {
                    string? baseUrl = XStr(cfg, "base_url");
                    string root = Paths.Join(d, string.IsNullOrEmpty(baseUrl) ? "." : baseUrl);
                    if (cfg["paths"] is JsonObject paths)
                        foreach (var (pat, targets) in paths)
                        {
                            string pre = pat.TrimEnd('*');
                            if (spec.StartsWith(pre, StringComparison.Ordinal) && (pat.EndsWith('*') || spec == pat))
                                foreach (var t in StrList(targets))
                                    cands.Add(Paths.Join(root, t.TrimEnd('*') + spec[pre.Length..]));
                        }
                    if (!string.IsNullOrEmpty(baseUrl)) cands.Add(Paths.Join(root, spec));
                    break;
                }
                string parent = Paths.Dir(d);
                if (parent == d || parent.Length == 0) break;
                d = parent;
            }
            if (spec.StartsWith("@/") || spec.StartsWith("~/"))
            {
                string dir = baseDir;
                while (true)
                {
                    if (Paths.Base(dir).Equals("src", StringComparison.OrdinalIgnoreCase))
                    {
                        cands.Add(Paths.Join(dir, spec[2..]));
                        break;
                    }
                    string parent = Paths.Dir(dir);
                    if (parent == dir || parent.Length == 0) break;
                    dir = parent;
                }
            }
        }
        foreach (var c in cands)
        {
            foreach (var ext in JsExts)
                if (_byPath.TryGetValue(Paths.Key(c + ext), out var hit)) return hit.Id;
            foreach (var ext in JsExts.Skip(1))
                if (_byPath.TryGetValue(Paths.Key(Paths.Join(c, "index" + ext)), out var hit)) return hit.Id;
        }
        return null;
    }

    private string? JsSymbol(string? targetFile, string imported, int depth = 0)
    {
        if (targetFile is null || imported == "*" || depth > 5) return null;
        string? name = imported == "default" ? _jsDefault.GetValueOrDefault(targetFile) : imported;
        if (string.IsNullOrEmpty(name)) return null;
        var syms = _fileSyms.GetValueOrDefault(targetFile) ?? [];
        string low = name.ToLowerInvariant();
        if (syms.TryGetValue(low, out var sid)) return sid;
        foreach (var (q, s) in syms)
            if (LastPart(q) == low) return s;
        // follow re-exports:  export { default as X } from './x.vue'   /  import X ...; export { X }
        if (!_jsFacts.TryGetValue(targetFile, out var facts)) return null;
        if (facts.Reexports[imported != "default" ? imported : name] is JsonArray { Count: >= 2 } rx)
        {
            string? spec = rx[0]?.ToString();
            string inner = rx[1]?.ToString() ?? "";
            if (spec is null)
            {
                // import X from './x'; export { X } -> follow X's own import
                if (_jsImports.GetValueOrDefault(targetFile)?.GetValueOrDefault(inner.ToLowerInvariant()) is { File: not null } via)
                    return JsSymbol(via.File, via.Imported, depth + 1);
                return JsSymbol(targetFile, inner, depth + 1);
            }
            string? next = ResolveSpec(facts.Abs, spec);
            return next is not null ? JsSymbol(next, inner, depth + 1) : null;
        }
        foreach (var spec in facts.StarExports)
        {
            string? next = ResolveSpec(facts.Abs, spec);
            if (next is not null && JsSymbol(next, imported, depth + 1) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>Source entry file of one of our own npm packages (e.g. @acme/ui-components -> src/main.ts).</summary>
    private string? PackageEntry(string pkg)
    {
        if (_entryCache.TryGetValue(pkg, out var cached)) return cached;
        string? entry = null;
        foreach (var pid in _projByOutput.GetValueOrDefault(pkg.ToLowerInvariant()) ?? [])
        {
            if (!_projects.TryGetValue(pid, out var info) || info.Info.Format != "npm") continue;
            foreach (var rel in new[] { "src/main.ts", "src/index.ts", "index.ts", "src/main.js", "src/index.js", "index.js",
                         "src/lib.ts", "src/library.ts" })
                if (_byPath.TryGetValue(Paths.Key(Paths.Join(info.AbsDir, rel)), out var hit))
                {
                    entry = hit.Id;
                    break;
                }
            if (entry is not null) break;
        }
        _entryCache[pkg] = entry;
        return entry;
    }

    /// <summary>Returns true/false when handled through imports, null to fall back to generic resolution.</summary>
    private bool? ResolveJs(string fid, string caller, Ref rf)
    {
        string kind = rf.Kind, name = rf.Name ?? "";
        var lineAttr = ("line", (JsonNode?)(rf.Line == 0 ? null : rf.Line));
        var imports = _jsImports.GetValueOrDefault(fid) ?? [];
        string low = name.ToLowerInvariant();
        if (kind == "component")
        {
            if (imports.TryGetValue(low, out var hit) && hit.File is not null)
                return Edge(caller, JsSymbol(hit.File, hit.Imported) ?? hit.File, "uses_component", lineAttr);
            var tg = Pick(fid, _typeIndex.GetValueOrDefault(("js", low)) ?? []).Where(t => N(t).Kind == "component");
            return EdgeToTargets(caller, tg, "uses_component", lineAttr);
        }
        if (kind is "call" or "new" or "type" or "inherit" && imports.TryGetValue(low, out var imp))
        {
            string? tgt = JsSymbol(imp.File, imp.Imported);
            if (tgt is null) return false;
            string rel = kind switch
            {
                "new" => "instantiates",
                "type" => "uses_type",
                "inherit" => "inherits",
                _ => TypeKinds.Contains(N(tgt).Kind) ? "instantiates" : "calls",
            };
            return Edge(caller, tgt, rel, lineAttr);
        }
        if (kind == "member" && imports.TryGetValue((rf.Qual ?? "").ToLowerInvariant(), out var q))
        {
            if (q.File is null) return false;
            if (q.Imported == "*")
                return JsSymbol(q.File, name) is { } t && Edge(caller, t, "calls", lineAttr);
            if (JsSymbol(q.File, q.Imported) is { } owner)
            {
                string oname = N(owner).Name.ToLowerInvariant();
                var ms = (_members.GetValueOrDefault(("js", oname))?.GetValueOrDefault(low) ?? [])
                    .Where(m => _symFile.GetValueOrDefault(m) == q.File).ToList();
                if (ms.Count > 0) return EdgeToTargets(caller, ms, "calls", lineAttr);
                return Edge(caller, owner, "uses_type", lineAttr);
            }
            return Edge(caller, q.File, "imports", lineAttr);
        }
        return null;
    }

    /// <summary>Repo -> client-side routes: (normalised path segments, route node id, route name).</summary>
    private readonly Dictionary<string, List<(string[] Segs, string Id, string? Name)>> _clientRoutes = [];

    /// <summary>
    /// Client-side route tables (Vue Router ...): a node per route (route:&lt;repo&gt;:&lt;path&gt;) that routes_to its
    /// component, is contained in its parent route and redirects_to its redirect target.
    /// </summary>
    private void ClientRoutes()
    {
        var tables = _records.Where(r => r.Facts.Extra?["routes_table"] is JsonArray && _g.HasNode(r.Id)).ToList();
        foreach (var r in tables)
        {
            var list = GetOrAdd(_clientRoutes, r.Repo);
            foreach (var o in ((JsonArray)r.Facts.Extra!["routes_table"]!).OfType<JsonObject>())
            {
                string path = XStr(o, "path") ?? "/";
                string id = $"route:{r.Repo}:{path}";
                _g.Node(id, ("kind", "route"), ("name", path), ("route_name", XStr(o, "name")), ("meta", o["meta"]?.DeepClone()),
                    ("repo", r.Repo), ("file", r.Id), ("line", o["line"]?.DeepClone()));
                _g.AddEdge(r.Id, id, "defines");
                string norm = CodeGraph.Text.Routes.Normalize(path);
                list.Add((norm.Length > 0 ? norm.Split('/') : [], id, XStr(o, "name")));
                string? target = null;
                if (XStr(o, "component") is { } comp && _jsImports.GetValueOrDefault(r.Id)?.GetValueOrDefault(comp.ToLowerInvariant()) is var (file, imported) && file is not null)
                    target = JsSymbol(file, imported) ?? file;
                else if (XStr(o, "lazy") is { } spec && ResolveSpec(r.Abs, spec) is { } lazyFile)
                    target = JsSymbol(lazyFile, "default") ?? lazyFile;
                if (target is not null) _g.AddEdge(id, target, "routes_to");
            }
        }
        foreach (var r in tables)
            foreach (var o in ((JsonArray)r.Facts.Extra!["routes_table"]!).OfType<JsonObject>())
            {
                string id = $"route:{r.Repo}:{XStr(o, "path") ?? "/"}";
                if (XStr(o, "parent") is { } parent && _g.HasNode($"route:{r.Repo}:{parent}"))
                    _g.AddEdge($"route:{r.Repo}:{parent}", id, "contains");
                if (RouteTarget(r.Repo, XStr(o, "redirect"), XStr(o, "redirect_name")) is { } to && to != id)
                    _g.AddEdge(id, to, "redirects_to");
            }
    }

    /// <summary>The route of a repo a path or route name means: by name, else the best path match (literal segments
    /// first, `:param` matching anything); none when several match equally well.</summary>
    private string? RouteTarget(string repo, string? path, string? name)
    {
        if (_clientRoutes.GetValueOrDefault(repo) is not { } list) return null;
        if (name is not null)
        {
            var named = list.Where(x => x.Name == name).Select(x => x.Id).Distinct().ToList();
            return named.Count == 1 ? named[0] : null;
        }
        if (path is null) return null;
        string norm = CodeGraph.Text.Routes.Normalize(path);
        string[] segs = norm.Length > 0 ? norm.Split('/') : [];
        var best = new List<string>();
        int bestScore = -1;
        foreach (var (rs, id, _) in list)
        {
            if (rs.Length != segs.Length) continue;
            int score = 0;
            bool ok = true;
            for (int i = 0; i < segs.Length && ok; i++)
                if (rs[i] == segs[i]) score += 2;
                else if (rs[i] != "{}" && segs[i] != "{}") ok = false;
            if (!ok) continue;
            if (score > bestScore) { best = [id]; bestScore = score; }
            else if (score == bestScore && !best.Contains(id)) best.Add(id);
        }
        return best.Count == 1 ? best[0] : null;
    }

    /// <summary>HTTP routes served by controller actions.</summary>
    private void RouteIndex()
    {
        foreach (var r in _records)
            foreach (var sym in r.Facts.Symbols)
            {
                // MVC: Url.Action("Edit", "Orders") names OrdersController.Edit, whatever its route
                if (sym.Kind == "method" && sym.Parent is { } owner && owner.EndsWith("Controller", StringComparison.Ordinal)
                    && owner.Length > "Controller".Length)
                {
                    string action = $"sym:{r.Repo}:{r.Rel}#{owner}.{sym.Name}";
                    if (_g.HasNode(action))
                        GetOrAdd(_actions, $"{owner[..^"Controller".Length]}/{sym.Name}".ToLowerInvariant()).Add(action);
                }
                if (sym.Extra?["routes"] is not JsonArray { Count: > 0 } routes) continue;
                string qual = !string.IsNullOrEmpty(sym.Parent) ? $"{sym.Parent}.{sym.Name}" : sym.Name;
                string sid = $"sym:{r.Repo}:{r.Rel}#{qual}";
                if (_g.GetNode(sid) is not { } node) continue;
                var list = routes.OfType<JsonObject>().Select(x => (Verb: XStr(x, "verb"), Path: XStr(x, "path") ?? "")).ToList();
                node["routes"] = Arr(list.Select(x => (string.IsNullOrEmpty(x.Verb) ? "any" : x.Verb).ToUpperInvariant() + " /" + x.Path));
                foreach (var (verb, path) in list)
                {
                    string[] segs = path.Length > 0 ? path.Split('/') : [];
                    GetOrAdd(_routes, segs.Length).Add((verb, segs, sid));
                    for (int k = 1; k <= 3 && k < segs.Length && segs[k - 1] != "{}"; k++)
                        GetOrAdd(_prefixOwners, string.Join('/', segs[..k])).Add(r.Rel + "#" + sym.Parent);
                }
            }
        // a server-side base path (api, api/v1) is a prefix that routes of several controllers share; a prefix of
        // one controller only is its own name, which a call must not skip
        _basePaths = _prefixOwners.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key).ToHashSet();
    }


    /// <summary>
    /// Controller actions a called path can reach. Client and server often disagree on a base path ("/api", "/v1",
    /// "/backend/api" on one side only), so when the full path matches nothing, leading fixed segments are dropped
    /// from the call or from the route, fewest first. Parameters must line up, and what is left must match on at
    /// least one fixed segment. More than three equally good matches count as none.
    /// </summary>
    private List<string> MatchRoute(string? verb, string path)
    {
        string[] segs = path.Length > 0 ? path.Split('/') : [];
        static bool Fixed(IEnumerable<string> s) => s.All(x => x != "{}");
        for (int skip = 0; skip <= 3; skip++)
        {
            var best = new List<string>();
            int bestScore = -1;
            void Compare(string[] call, string[] route, string? rverb, string sid)
            {
                if (!string.IsNullOrEmpty(verb) && !string.IsNullOrEmpty(rverb) && !string.Equals(verb, rverb, StringComparison.OrdinalIgnoreCase)) return;
                int score = 0;
                for (int i = 0; i < call.Length; i++)
                {
                    if (call[i] == route[i]) score += 2;
                    else if (call[i] != "{}" && route[i] != "{}") return;
                }
                if (skip > 0 && score == 0) return; // after dropping a base path, a fixed segment must still match
                if (score > bestScore) { best = [sid]; bestScore = score; }
                else if (score == bestScore) best.Add(sid);
            }
            if (skip == 0)
                foreach (var (rverb, rsegs, sid) in _routes.GetValueOrDefault(segs.Length) ?? [])
                    Compare(segs, rsegs, rverb, sid);
            else
            {
                // the call has an extra base path
                if (skip < segs.Length && Fixed(segs[..skip]) && segs[skip..].Any(s => s != "{}"))
                    foreach (var (rverb, rsegs, sid) in _routes.GetValueOrDefault(segs.Length - skip) ?? [])
                        Compare(segs[skip..], rsegs, rverb, sid);
                // the route has one (shared by several controllers, so not a controller's own name)
                if (segs.Any(s => s != "{}"))
                    foreach (var (rverb, rsegs, sid) in _routes.GetValueOrDefault(segs.Length + skip) ?? [])
                        if (_basePaths.Contains(string.Join('/', rsegs[..skip]))) Compare(segs, rsegs[skip..], rverb, sid);
            }
            if (best.Count > 0) return best.Count <= 3 ? best : [];
        }
        return [];
    }

    /// <summary>MVC convention: Views/&lt;Controller&gt;/&lt;Action&gt;.cshtml is rendered by &lt;Controller&gt;Controller.&lt;Action&gt;.</summary>
    private void ViewLinks()
    {
        foreach (var n in _g.Nodes.ToList())
        {
            if (n.Kind != "view" || n.Str("file") is not { } file || NodeStr(file, "path") is not { } rel) continue;
            var parts = rel.Replace('\\', '/').Split('/');
            if (parts.Length < 2 || parts[^2].Equals("shared", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var sid in MatchRoute(null, $"{parts[^2]}/{n.Name}".ToLowerInvariant()))
                _g.AddEdge(sid, n.Id, "renders", attrs: ("via", "MVC convention"));
        }
    }

    private bool ResolveShips(string fid, string caller, Ref rf)
    {
        string name = rf.Name ?? "";
        string? hint = XStr(rf.Extra, "project"), shipKind = XStr(rf.Extra, "ship_kind");
        var lineAttr = ("line", (JsonNode?)(rf.Line == 0 ? null : rf.Line));
        var targets = new HashSet<string>();
        if (!string.IsNullOrEmpty(hint)) targets.UnionWith(_projByOutput.GetValueOrDefault(hint.ToLowerInvariant()) ?? []);
        string low = name.ToLowerInvariant();
        string ext = Paths.Ext(low);
        if (low.Length > 0 && targets.Count == 0 && ShipBinaries.Contains(ext))
            targets.UnionWith(_projByOutput.GetValueOrDefault(Paths.Stem(low)) ?? []);
        if (targets.Count == 0 && low.Length > 0)
        {
            var files = (_byBasename.GetValueOrDefault(low) ?? []).Select(c => c.Id).ToList();
            targets.UnionWith(files.Count > 1 ? Pick(fid, files) : files);
        }
        if (targets.Count == 0 && ShipBinaries.Contains(ext) && External(name, shipKind ?? "file") is { } e)
            targets.Add(e);
        bool ok = false;
        foreach (var t in targets)
            if (Edge(caller, t, "ships", lineAttr, ("via", shipKind))) ok = true;
        return ok;
    }
}
