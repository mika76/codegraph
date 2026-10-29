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

    private readonly Dictionary<string, List<(string Id, string Kind, string? Repo)>> _idx = [];

    public NameIndex(Graph g)
    {
        foreach (var n in g.Nodes)
        {
            var keys = new List<string?> { n.Id, n.Str("name") };
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
            // a routine as Type.Member: unique where the bare name is not
            else if (n.Id.StartsWith("sym:", StringComparison.Ordinal) && n.Str("qual") is { } qual && qual != n.Str("name"))
                keys.Add(qual);
            foreach (var k in keys)
                if (!string.IsNullOrEmpty(k))
                    Add(k, n.Id, n.Kind, n.Str("repo"));
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

    public void Add(string name, string id, string kind, string? repo = null)
    {
        var key = name.ToLowerInvariant();
        if (!_idx.TryGetValue(key, out var l)) _idx[key] = l = [];
        l.Add((id, kind, repo));
    }

    private List<(string Id, string Kind, string? Repo)> Hits(string name) =>
        _idx.TryGetValue(name.Trim().ToLowerInvariant(), out var l) ? l : [];

    /// <summary>
    /// The node a name means: by kind priority, then (for a name written in a repo's own knowledge file) a node of
    /// that repo before the same kind elsewhere.
    /// </summary>
    public string? Find(string name, string? repo = null)
    {
        var hits = Hits(name);
        if (hits.Count == 0) return null;
        // stable sort by kind priority
        return hits.Select((h, i) => (h, i))
            .OrderBy(x => { int p = Array.IndexOf(KindPriority, x.h.Kind); return p < 0 ? 99 : p; })
            .ThenBy(x => repo is not null && string.Equals(x.h.Repo, repo, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.i).First().h.Id;
    }

    public List<string> FindAll(string name) => Hits(name).Select(h => h.Id).ToList();
}

/// <summary>
/// Knowledge entries to look at again. Names are compared with what they matched at the last build (kept in
/// <see cref="FileName"/> next to graph.json), so a renamed or removed project, table or file is noticed even when
/// its old name now quietly becomes a concept.
/// </summary>
public sealed class KnowledgeReview
{
    public const string FileName = "knowledge-state.json";

    /// <summary>"repo|name" (repo empty for central files) -> node id, from the last build.</summary>
    public Dictionary<string, string> Previous { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Current { get; } = new(StringComparer.Ordinal);
    public List<string> Items { get; } = [];

    public static KnowledgeReview Load(string path)
    {
        var r = new KnowledgeReview();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o["names"] is JsonObject names)
                foreach (var kv in names)
                    if (kv.Value is JsonValue v && v.TryGetValue(out string? id)) r.Previous[kv.Key] = id;
        }
        catch (JsonException) { } // a damaged state file only costs one build's comparison
        return r;
    }

    public void Save(string path)
    {
        var names = new JsonObject();
        foreach (var kv in Current.OrderBy(kv => kv.Key, StringComparer.Ordinal)) names[kv.Key] = kv.Value;
        File.WriteAllText(path, new JsonObject { ["names"] = names }.ToJsonString());
    }
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
    /// <summary>
    /// The knowledge files of a config: the central <c>knowledge</c> patterns, then each repo's
    /// <c>repo_knowledge</c> files (with the repo they belong to). Patterns may contain * and ? in the file name part.
    /// </summary>
    public static List<(string Path, string? Repo)> KnowledgeFiles(Config cfg)
    {
        var files = new List<(string, string?)>();
        foreach (var p in cfg.Knowledge)
            files.AddRange(Glob(p).Select(f => (f, (string?)null)));
        foreach (var r in cfg.Repos)
            foreach (var p in cfg.RepoKnowledge)
                files.AddRange(Glob(Path.Combine(r.Path, p)).Select(f => (f, (string?)r.Name)));
        return files;
    }

    /// <summary>The file <c>remember</c> writes facts about a repo's nodes to (the first repo_knowledge name without wildcards).</summary>
    public static string? RepoKnowledgeFile(Config cfg, string repoPath) =>
        cfg.RepoKnowledge.FirstOrDefault(p => p.IndexOfAny(['*', '?']) < 0) is { } p ? Path.Combine(repoPath, p) : null;

    /// <summary>The file <c>remember</c> writes everything else to (the first knowledge entry without wildcards).</summary>
    public static string? CentralKnowledgeFile(Config cfg) =>
        cfg.Knowledge.FirstOrDefault(p => p.IndexOfAny(['*', '?']) < 0);

    /// <summary>Apply knowledge files; patterns may contain * and ? in the file name part.</summary>
    public static void ApplyKnowledge(Graph g, IEnumerable<string>? patterns, List<string> warnings) =>
        ApplyKnowledge(g, (patterns ?? []).SelectMany(Glob).Select(f => (f, (string?)null)).ToList(), warnings);

    /// <summary>
    /// Apply knowledge files. Names in a repo's own file prefer that repo's nodes. A name with a path separator that
    /// is not in the graph (a typo, or a repo this graph does not scan) is skipped instead of becoming a concept.
    /// Entries worth a second look go to <paramref name="review"/> (or to the warnings without one): such paths,
    /// names that matched a node at the last build and match nothing now, and facts the graph now links by itself.
    /// </summary>
    public static void ApplyKnowledge(Graph g, List<(string Path, string? Repo)> files, List<string> warnings,
        KnowledgeReview? review = null)
    {
        if (files.Count == 0) return;
        var names = new NameIndex(g);
        string fname = "", repo = "";
        var items = review?.Items ?? warnings;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Item(string text) { if (seen.Add(text)) items.Add(text); }
        // links the builder made (before any fact is added), either direction
        var linked = new Dictionary<(string, string), string>();
        foreach (var e in g.Edges)
            if (e.Source != e.Target && !(e.Data["custom"] is JsonValue cv && cv.TryGetValue(out bool c) && c))
                linked.TryAdd((e.Source, e.Target), e.Relation);

        string? Resolve(string name, bool create = true)
        {
            string key = $"{repo}|{Slug(name).ToLowerInvariant()}";
            var nid = names.Find(name, repo.Length > 0 ? repo : null);
            if (nid is not null && g.GetNode(nid)!.Kind != "concept")
            {
                if (review is not null) review.Current[key] = nid;
                return nid;
            }
            string? before = review?.Previous.GetValueOrDefault(key);
            if (before is not null)
            {
                review!.Current[key] = before; // keep asking until the entry is fixed or removed
                Item($"{fname}: '{name}' matched {{{before}}} at the last build and matches nothing now (renamed or removed?)");
            }
            if (nid is not null) return nid;
            if (!create) return null;
            if (name.IndexOfAny(['/', '\\']) >= 0)
            {
                if (before is null)
                    Item($"{fname}: '{name}' is not in the graph (a typo, or a repo this graph does not scan) - skipped");
                return null;
            }
            string cid = $"concept:{Slug(name).ToLowerInvariant()}";
            g.Node(cid, ("kind", "concept"), ("name", Slug(name)));
            names.Add(Slug(name), cid, "concept");
            return cid;
        }

        foreach (var (path, fileRepo) in files)
        {
            repo = fileRepo ?? "";
            fname = fileRepo is null ? Path.GetFileName(path) : $"{fileRepo}: {Path.GetFileName(path)}";
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
                    if (Resolve(subjS) is not { } s) continue;
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
                        if (linked.TryGetValue((s, t), out var known) || linked.TryGetValue((t, s), out known))
                            Item($"{fname}: fact '{subjS} {relS} {objS}' - the graph links these itself now ({known}); the fact may be redundant");
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
                    if (Resolve(kv.Key) is not { } nid) continue;
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
    // R2.4.0, release/2.4, releases/v2.4.1, hotfix/2.4.1-rc1, R2_4_0, release/2024-10, v2.0 ... and a single
    // number only with an explicit release prefix (release-35); feature/... branches never count
    private static readonly Regex BranchRe = new(
        @"^(?:(?:(?:release|releases|rel|hotfix|support)[/_-]?[vr]?|[rv])?(\d+(?:[._-]\d+)+)" +
        @"|(?:release|releases|rel)[/_-]?[vr]?(\d+))(?:[-+][A-Za-z][\w.-]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The release version a branch name stands for (separators normalised to dots), or null.</summary>
    public static string? BranchVersion(string branch)
    {
        var m = BranchRe.Match(branch);
        if (!m.Success) return null;
        string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return v.Replace('_', '.').Replace('-', '.');
    }

    /// <summary>
    /// The checked-out branch of a repo, read from .git/HEAD (no git CLI needed); a detached HEAD gives the
    /// first 12 characters of the commit id; null when there is no .git/HEAD. A .git file (worktree, submodule)
    /// is followed to its "gitdir:".
    /// </summary>
    public static string? GitBranch(string repoPath)
    {
        try
        {
            if (GitDir(repoPath) is not { } gitDir) return null;
            string head = File.ReadAllText(Path.Combine(gitDir, "HEAD"), Encoding.UTF8).Trim();
            const string marker = "refs/heads/";
            int i = head.IndexOf(marker, StringComparison.Ordinal);
            return i >= 0 ? head[(i + marker.Length)..] : head[..Math.Min(12, head.Length)];
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The repo's git folder: .git, or where a .git file (worktree, submodule) points; null when none.</summary>
    private static string? GitDir(string repoPath)
    {
        string gitDir = Path.Combine(repoPath, ".git");
        if (File.Exists(gitDir))
        {
            string link = File.ReadAllText(gitDir, Encoding.UTF8).Trim();
            if (!link.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(Path.Combine(repoPath, link["gitdir:".Length..].Trim()));
        }
        return Directory.Exists(gitDir) ? gitDir : null;
    }

    /// <summary>
    /// The commit id checked out in a repo, read from .git (HEAD, the branch's ref file or packed-refs; a worktree's
    /// refs are in its common folder); null when it can't be read.
    /// </summary>
    public static string? GitCommit(string repoPath)
    {
        try
        {
            if (GitDir(repoPath) is not { } gitDir) return null;
            string head = File.ReadAllText(Path.Combine(gitDir, "HEAD"), Encoding.UTF8).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return head.Length >= 40 ? head : null;
            string refName = head[4..].Trim();
            string common = File.Exists(Path.Combine(gitDir, "commondir"))
                ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(Path.Combine(gitDir, "commondir"), Encoding.UTF8).Trim()))
                : gitDir;
            foreach (var dir in new[] { gitDir, common })
            {
                string file = Path.Combine(dir, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file)) return File.ReadAllText(file, Encoding.UTF8).Trim();
            }
            string packed = Path.Combine(common, "packed-refs");
            if (File.Exists(packed))
                foreach (var line in File.ReadLines(packed))
                    if (line.EndsWith(" " + refName, StringComparison.Ordinal)) return line.Split(' ')[0];
            return null;
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
            if (BranchVersion(branch) is not { } version) continue;
            string rid = $"rel:{version}";
            if (!g.HasNode(rid))
                g.Node(rid, ("kind", "release"), ("name", version), ("status", $"from branch {branch}"));
            foreach (var n in g.Nodes.Where(n => n.Kind == "project" && n.Str("repo") == rn.Name && !n.Bool("test")).ToList())
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
