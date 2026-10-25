using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;

namespace CodeGraph.Knowledge;

/// <summary>
/// Find graph nodes by the names people use: 'Order Entry', 'OrderEntry.exe', 'Orders', 'frmMain.frm'...
/// </summary>
public sealed class NameIndex
{
    public static readonly string[] KindPriority =
    [
        "project", "area", "db_table", "db_view", "db_proc", "db_function", "db_trigger", "file",
        "library", "solution", "form", "class", "module", "usercontrol", "interface", "struct", "concept", "release",
    ];

    private readonly Dictionary<string, List<(string Id, string Kind)>> _idx = [];

    public NameIndex(Graph g)
    {
        foreach (var n in g.Nodes)
        {
            var keys = new List<string?> { n.Str("name") };
            if (n.Kind == "project")
            {
                keys.Add(n.Str("internal_name"));
                keys.Add(n.Str("output"));
                keys.Add(n.Str("assembly"));
                if (!string.IsNullOrEmpty(n.Str("output")))
                    keys.Add(SplitExt(n.Str("output")!));
            }
            else if (n.Kind == "file")
                keys.Add(n.Str("path"));
            foreach (var k in keys)
                if (!string.IsNullOrEmpty(k))
                    Add(k, n.Id, n.Kind);
        }
    }

    // os.path.splitext: drops the last ".ext" of the final path component (a leading dot is not an extension)
    private static string SplitExt(string p)
    {
        int slash = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
        int dot = p.LastIndexOf('.');
        if (dot <= slash) return p;
        // all dots at the start of the name do not count
        int i = slash + 1;
        while (i < dot && p[i] == '.') i++;
        return i >= dot ? p : p[..dot];
    }

    public void Add(string name, string id, string kind)
    {
        var key = name.ToLowerInvariant();
        if (!_idx.TryGetValue(key, out var l)) _idx[key] = l = [];
        l.Add((id, kind));
    }

    private List<(string Id, string Kind)> Hits(string name) =>
        _idx.TryGetValue(name.Trim().ToLowerInvariant(), out var l) ? l : [];

    public string? Find(string name)
    {
        var hits = Hits(name);
        if (hits.Count == 0) return null;
        // stable sort by kind priority
        return hits.Select((h, i) => (h, i))
            .OrderBy(x => { int p = Array.IndexOf(KindPriority, x.h.Kind); return p < 0 ? 99 : p; })
            .ThenBy(x => x.i).First().h.Id;
    }

    public List<string> FindAll(string name) => Hits(name).Select(h => h.Id).ToList();
}

/// <summary>Extra knowledge layered on top of the code graph: releases and free-form facts/notes.</summary>
public static class KnowledgeLoader
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads a JSON-with-comments file; returns null for an empty file.</summary>
    public static JsonNode? LoadFile(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text, documentOptions: JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new CodeGraphException($"{path}: {ex.Message}");
        }
    }

    internal static string AsString(JsonNode? n) =>
        n is null ? "" : n is JsonValue v && v.TryGetValue(out string? s) ? s! : n.ToJsonString();

    // Python truthiness for a value taken from a JSON file.
    internal static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue(out string? s) => s.Length > 0,
        JsonValue v when v.TryGetValue(out bool b) => b,
        JsonValue v when v.TryGetValue(out double d) => d != 0,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => true,
    };

    private static string? StrOrNull(JsonNode? n) => n is null ? null : AsString(n);

    private static string Slug(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // ------------------------------------------------------------------ releases
    public static void ApplyReleases(Graph g, string? path, List<string> warnings)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var data = LoadFile(path);
        JsonArray? releases = data switch
        {
            JsonArray a => a,
            JsonObject o => o["releases"] as JsonArray,
            _ => null,
        };
        if (releases is null) return;
        var names = new NameIndex(g);
        foreach (var item in releases)
        {
            if (item is not JsonObject rel)
            {
                warnings.Add("releases: an entry without 'version' was skipped");
                continue;
            }
            string version = (Truthy(rel["version"]) ? AsString(rel["version"]) : Truthy(rel["name"]) ? AsString(rel["name"]) : "").Trim();
            if (version.Length == 0)
            {
                warnings.Add("releases: an entry without 'version' was skipped");
                continue;
            }
            string rid = $"rel:{version}";
            g.Node(rid, ("kind", "release"), ("name", version), ("date", StrOrNull(rel["date"])),
                ("notes", StrOrNull(rel["notes"])), ("status", StrOrNull(rel["status"])));

            var items = new List<(string Name, string? Version)>();
            switch (rel["modules"])
            {
                case JsonArray arr:
                    foreach (var m in arr)
                    {
                        if (m is JsonObject mo)
                            foreach (var kv in mo) items.Add((kv.Key, kv.Value is null ? null : AsString(kv.Value)));
                        else if (m is not null) items.Add((AsString(m), null));
                    }
                    break;
                case JsonObject obj:
                    foreach (var kv in obj) items.Add((kv.Key, kv.Value is null ? null : AsString(kv.Value)));
                    break;
            }
            foreach (var (modName, modVer) in items)
            {
                var targets = names.FindAll(modName).Distinct()
                    .Where(t => g.GetNode(t)!.Kind is "project" or "area" or "file" || g.GetNode(t)!.Kind.StartsWith("db_", StringComparison.Ordinal))
                    .ToList();
                var projects = targets.Where(t => g.GetNode(t)!.Kind == "project").ToList();
                targets = projects.Count > 0 ? projects : targets.Take(1).ToList();
                if (targets.Count == 0)
                {
                    string cid = $"module:{modName.ToLowerInvariant()}";
                    g.Node(cid, ("kind", "module_ref"), ("name", modName));
                    targets = [cid];
                    warnings.Add($"releases {version}: module '{modName}' did not match any project/folder/object (kept as a plain name)");
                }
                foreach (var t in targets)
                    AddRelease(g, t, rid, modVer, null);
            }
        }
    }

    // ------------------------------------------------------------------ facts and notes
    /// <summary>Apply knowledge files; patterns may contain * and ? in the file name part.</summary>
    public static void ApplyKnowledge(Graph g, IEnumerable<string>? patterns, List<string> warnings)
    {
        var files = new List<string>();
        foreach (var p in patterns ?? [])
            files.AddRange(Glob(p));
        if (files.Count == 0) return;
        var names = new NameIndex(g);

        string? Resolve(string name, bool create = true)
        {
            var nid = names.Find(name);
            if (nid is not null) return nid;
            if (!create) return null;
            string cid = $"concept:{Slug(name).ToLowerInvariant()}";
            g.Node(cid, ("kind", "concept"), ("name", Slug(name)));
            names.Add(Slug(name), cid, "concept");
            return cid;
        }

        foreach (var path in files)
        {
            string fname = Path.GetFileName(path);
            if (LoadFile(path) is not JsonObject data) continue;
            if (data["facts"] is JsonArray facts)
            {
                foreach (var fact in facts)
                {
                    JsonNode? subj, relation, obj, note;
                    if (fact is JsonArray fa && fa.Count >= 3)
                    {
                        subj = fa[0]; relation = fa[1]; obj = fa[2];
                        note = fa.Count > 3 ? fa[3] : null;
                    }
                    else if (fact is JsonObject fo)
                    {
                        subj = fo["subject"]; relation = fo["relation"]; obj = fo["object"]; note = fo["note"];
                    }
                    else
                    {
                        warnings.Add($"{fname}: could not read fact {fact?.ToJsonString() ?? "null"}");
                        continue;
                    }
                    if (!Truthy(subj) || !Truthy(relation)) continue;
                    string subjS = AsString(subj), relS = AsString(relation);
                    string s = Resolve(subjS)!;
                    string rel = Regex.Replace(relS, @"[^\w ]+", "").Trim().ToLowerInvariant().Replace(' ', '_');
                    if (rel.Length == 0) rel = "related_to";
                    string objS = Truthy(obj) ? AsString(obj) : "";
                    if (objS.Length > 0 && Regex.IsMatch(objS, @"^https?://"))
                    {
                        var sn = g.GetNode(s)!;
                        if (sn["links"] is not JsonArray links) sn["links"] = links = [];
                        links.Add((JsonNode)new JsonObject
                        {
                            ["relation"] = relS,
                            ["url"] = objS,
                            ["note"] = note?.DeepClone(),
                        });
                        continue;
                    }
                    string? t = objS.Length > 0 ? Resolve(objS) : null;
                    if (t is not null)
                    {
                        var e = g.AddEdge(s, t, rel, attrs: [("custom", JsonValue.Create(true)), ("label", relS), ("note", note?.DeepClone())]);
                        if (e is null && s == t)
                            warnings.Add($"{fname}: fact '{subjS} {relS} {objS}' links a node to itself");
                    }
                }
            }
            if (data["notes"] is JsonObject notes)
            {
                foreach (var kv in notes)
                {
                    string nid = Resolve(kv.Key)!;
                    var n = g.GetNode(nid)!;
                    if (n["knowledge_notes"] is not JsonArray kn) n["knowledge_notes"] = kn = [];
                    kn.Add((JsonNode)JsonValue.Create(AsString(kv.Value).Trim())!);
                }
            }
        }
    }

    private static List<string> Glob(string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) >= 0)
        {
            string dir = Path.GetDirectoryName(pattern) ?? "";
            string filePat = Path.GetFileName(pattern);
            string searchDir = dir.Length == 0 ? "." : dir;
            if (Directory.Exists(searchDir) && filePat.IndexOfAny(['*', '?']) >= 0 && dir.IndexOfAny(['*', '?']) < 0)
            {
                var found = Directory.EnumerateFiles(searchDir, filePat)
                    .Select(f => dir.Length == 0 ? Path.GetFileName(f) : Path.Combine(dir, Path.GetFileName(f)))
                    .Order(StringComparer.Ordinal).ToList();
                if (found.Count > 0) return found;
            }
        }
        return File.Exists(pattern) ? [pattern] : [];
    }

    // ------------------------------------------------------------------ release snapshot
    /// <summary>
    /// Append a release entry listing every project and the version found in source to releases.json.
    /// The file is rewritten (indented), so comments in it are lost. Returns the number of projects.
    /// </summary>
    public static int ReleaseSnapshot(Graph g, string version, string? date, string path)
    {
        JsonNode? root = File.Exists(path) ? LoadFile(path) : null;
        root ??= new JsonObject { ["releases"] = new JsonArray() };
        JsonArray list;
        if (root is JsonArray ra) list = ra;
        else
        {
            var ro = root.AsObject();
            if (ro["releases"] is not JsonArray la) ro["releases"] = la = [];
            list = la;
        }
        var modules = new JsonObject();
        var projects = g.Nodes.Where(n => n.Kind == "project").OrderBy(n => n.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        foreach (var n in projects)
            modules[n.Name] = n.Str("version") ?? "";
        list.Add((JsonNode)new JsonObject
        {
            ["version"] = version,
            ["date"] = date ?? DateTime.Today.ToString("yyyy-MM-dd"),
            ["notes"] = "",
            ["modules"] = modules,
        });
        using (var fs = File.Create(path))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = JsonOpts.Relaxed }))
            root.WriteTo(w);
        return projects.Count;
    }

    // ------------------------------------------------------------------ branches
    private static readonly Regex BranchRe = new(@"^(?:release[/_-]?|rel[/_-]?|r|v)?(\d+(?:\.\d+)+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The checked-out branch of a repo, read from .git/HEAD (no git CLI needed); a detached HEAD gives the
    /// first 12 characters of the commit id; null when there is no .git/HEAD. (Python <c>_git_branch</c>.)
    /// </summary>
    public static string? GitBranch(string repoPath)
    {
        try
        {
            string head = File.ReadAllText(Path.Combine(repoPath, ".git", "HEAD"), Encoding.UTF8).Trim();
            const string marker = "refs/heads/";
            int i = head.IndexOf(marker, StringComparison.Ordinal);
            return i >= 0 ? head[(i + marker.Length)..] : head[..Math.Min(12, head.Length)];
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>A repo checked out on a release branch (e.g. R2.4.0) -> its projects are part of release 2.4.0.</summary>
    public static void ApplyBranchReleases(Graph g)
    {
        foreach (var rn in g.Nodes.Where(n => n.Kind == "repo").ToList())
        {
            string branch = rn.Str("branch") ?? "";
            var m = BranchRe.Match(branch);
            if (!m.Success) continue;
            string version = m.Groups[1].Value;
            string rid = $"rel:{version}";
            if (!g.HasNode(rid))
                g.Node(rid, ("kind", "release"), ("name", version), ("status", $"from branch {branch}"));
            foreach (var n in g.Nodes.Where(n => n.Kind == "project" && n.Str("repo") == rn.Name).ToList())
            {
                string? pv = n.Str("version");
                AddRelease(g, n.Id, rid, string.IsNullOrEmpty(pv) ? null : pv, "branch");
            }
        }
    }

    /// <summary>
    /// Record that a module is part of a release. Membership is a fact, not an occurrence: when another source
    /// (branch name, releases.json) already recorded it, the count stays 1. A version from releases.json replaces
    /// the project version taken for a branch release; otherwise the first version stays.
    /// </summary>
    private static void AddRelease(Graph g, string module, string rid, string? version, string? via)
    {
        if (g.GetEdge(module, rid, "released_in") is { } e)
        {
            if (version is not null && via is null && e.Str("via") == "branch") e["version"] = version;
            return;
        }
        g.AddEdge(module, rid, "released_in", attrs: [("version", version), ("via", via)]);
    }
}

internal static class JsonOpts
{
    public static readonly System.Text.Encodings.Web.JavaScriptEncoder Relaxed =
        System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
}
