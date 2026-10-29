using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeGraph.Model;
using CodeGraph.Query;

namespace CodeGraph.Knowledge;

/// <summary>Where <c>remember</c> writes: each repo's own knowledge file, and a central file for everything else.</summary>
public sealed record KnowledgeTargets(string? Central, IReadOnlyDictionary<string, string> Repos)
{
    public static KnowledgeTargets From(Config cfg) => new(
        KnowledgeLoader.CentralKnowledgeFile(cfg),
        cfg.Repos.Select(r => (r.Name, File: KnowledgeLoader.RepoKnowledgeFile(cfg, r.Path)))
            .Where(x => x.File is not null)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().File!, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Adds a fact or note to a knowledge file. The file is JSON with comments, so the entry is inserted as text before
/// the closing bracket of its section: everything already in the file stays exactly as it was.
/// </summary>
public static class KnowledgeWriter
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Record a fact (subject, relation, object, optional note) or, without a relation, a note on the subject.
    /// A subject in a repo goes to that repo's knowledge file, anything else to the central one. Returns what was
    /// written and where.
    /// </summary>
    public static string Remember(Graph g, KnowledgeTargets targets, string subject, string? relation, string? obj,
        string? note, string source, string? date = null)
    {
        subject = subject.Trim();
        relation = string.IsNullOrWhiteSpace(relation) ? null : relation.Trim();
        obj = string.IsNullOrWhiteSpace(obj) ? null : obj.Trim();
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (relation is null && note is null)
            throw new CodeGraphException("Give a relation and an object (a fact), or a note.");
        if (relation is not null && obj is null)
            throw new CodeGraphException("A fact needs an object.");

        string? sid = Node(g, subject);
        string? repo = sid is null ? null : RepoOf(g, sid);
        string file = repo is not null && targets.Repos.TryGetValue(repo, out var rf) ? rf
            : targets.Central ?? throw new CodeGraphException(
                "No knowledge file to write to: set \"knowledge\" (or \"repo_knowledge\") in codegraph.json.");
        // names are read back in the context of the file they are written to
        string? fileRepo = repo is not null && targets.Repos.ContainsKey(repo) ? repo : null;
        var names = new NameIndex(g);
        string subjName = sid is null ? subject : NameFor(g, names, sid, fileRepo);

        string text = File.Exists(file) ? Read(file, out _) : "";
        if (text.Trim().Length == 0)
            text = "// Facts and notes for codegraph: a fact is [subject, relation, object (, note)], a note is \"name\": \"text\".\n" +
                   "// Names are project, file, folder or database object names, or node ids. Commit this file.\n{\n}\n";
        var existing = text.Trim().Length == 0 ? null : Parse(text, file);
        string comment = "// " + string.Join(" ", source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim() +
                         $" ({date ?? DateTime.Today.ToString("yyyy-MM-dd")})";
        string entry, result;
        if (relation is not null)
        {
            string objName = obj!;
            if (!obj!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !obj.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && Node(g, obj) is { } oid)
                objName = NameFor(g, names, oid, fileRepo);
            var parts = new List<string> { subjName, relation, objName };
            if (note is not null) parts.Add(note);
            if (existing?["facts"] is JsonArray facts && facts.Any(f => f is JsonArray fa && fa.Count >= 3 &&
                    Enumerable.Range(0, 3).All(i => string.Equals(KnowledgeLoader.AsString(fa[i]).Trim(), parts[i], StringComparison.OrdinalIgnoreCase))))
                return $"Already recorded in {file}.";
            entry = "[" + string.Join(", ", parts.Select(Quote)) + "]";
            result = AddToSection(text, "facts", '[', entry, comment);
        }
        else
        {
            var notes = existing?["notes"] as JsonObject;
            string? key = notes?.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k.Trim(), subjName, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
            {
                string old = KnowledgeLoader.AsString(notes![key]);
                if (old.Contains(note!, StringComparison.OrdinalIgnoreCase)) return $"Already recorded in {file}.";
                entry = $"{Quote(key)}: {Quote(old.TrimEnd() + "\n\n" + note)}";
                result = ExtendNote(text, key, old.TrimEnd() + "\n\n" + note!, comment);
            }
            else
            {
                entry = $"{Quote(subjName)}: {Quote(note!)}";
                result = AddToSection(text, "notes", '{', entry, comment);
            }
        }
        Parse(result, file); // never write a file the build could not read
        Write(file, result);
        return $"Recorded in {file}:\n  {entry}\nRun `codegraph build` to see it in the graph, and commit the file.";
    }

    /// <summary>The node a name means, null when nothing matches (a new concept); throws when it is ambiguous.</summary>
    private static string? Node(Graph g, string name)
    {
        if (name.Contains('*')) throw new CodeGraphException($"'{name}': no wildcards here - use the exact name or id.");
        var m = GraphQuery.Resolve(g, name);
        if (m.Id is not null) return m.Id;
        if (m.Matches.Count > 0) throw new CodeGraphException(GraphQuery.Problem(g, name, m) + "Call again with one of the ids.");
        if (name.IndexOfAny(['/', '\\']) >= 0)
            throw new CodeGraphException($"'{name}' is not in the graph. Use search to find its exact path or id.");
        return null;
    }

    private static string? RepoOf(Graph g, string id)
    {
        var n = g.GetNode(id)!;
        if (n.Str("repo") is { Length: > 0 } r) return r;
        return n.Str("file") is { Length: > 0 } f && g.GetNode(f) is { } fn ? fn.Str("repo") : null;
    }

    /// <summary>
    /// A readable name that the loader resolves back to the same node, else the node id. Symbols are written as
    /// Type.Member: a bare routine name that is unique today becomes ambiguous when another routine gets it.
    /// </summary>
    private static string NameFor(Graph g, NameIndex names, string id, string? repo)
    {
        var n = g.GetNode(id)!;
        string? name = n.Kind == "file" ? n.Str("path") : id.StartsWith("sym:", StringComparison.Ordinal) ? n.Str("qual") ?? n.Str("name") : n.Str("name");
        return !string.IsNullOrEmpty(name) && names.Find(name, repo) == id ? name : id;
    }

    // ------------------------------------------------------------------ text editing

    private enum T { Open, Close, Colon, Comma, String, Literal }

    private readonly record struct Tok(T Kind, int Start, int End, char Ch);

    /// <summary>Tokens of a JSON-with-comments text (comments and white space skipped).</summary>
    private static List<Tok> Lex(string s)
    {
        var toks = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? s.Length : e + 2;
                continue;
            }
            if (c is '{' or '[') { toks.Add(new(T.Open, i, i + 1, c)); i++; continue; }
            if (c is '}' or ']') { toks.Add(new(T.Close, i, i + 1, c)); i++; continue; }
            if (c == ':') { toks.Add(new(T.Colon, i, i + 1, c)); i++; continue; }
            if (c == ',') { toks.Add(new(T.Comma, i, i + 1, c)); i++; continue; }
            if (c == '"')
            {
                int j = i + 1;
                while (j < s.Length && s[j] != '"') j += s[j] == '\\' ? 2 : 1;
                toks.Add(new(T.String, i, Math.Min(j + 1, s.Length), c));
                i = j + 1;
                continue;
            }
            int k = i;
            while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not (',' or ':' or '{' or '}' or '[' or ']' or '/')) k++;
            toks.Add(new(T.Literal, i, Math.Max(k, i + 1), c));
            i = Math.Max(k, i + 1);
        }
        return toks;
    }

    private static int Matching(List<Tok> toks, int open)
    {
        int depth = 0;
        for (int i = open; i < toks.Count; i++)
        {
            if (toks[i].Kind == T.Open) depth++;
            else if (toks[i].Kind == T.Close && --depth == 0) return i;
        }
        throw new CodeGraphException("The knowledge file has an unclosed bracket.");
    }

    /// <summary>The root object's members: key token index and value token index.</summary>
    private static List<(int Key, int Value)> Members(string s, List<Tok> toks, out int rootClose)
    {
        if (toks.Count == 0 || toks[0].Ch != '{') throw new CodeGraphException("The knowledge file must hold one { ... } object.");
        rootClose = Matching(toks, 0);
        var list = new List<(int, int)>();
        int i = 1;
        while (i < rootClose)
        {
            if (toks[i].Kind == T.String && i + 2 <= rootClose && toks[i + 1].Kind == T.Colon)
            {
                int v = i + 2;
                list.Add((i, v));
                i = (toks[v].Kind == T.Open ? Matching(toks, v) : v) + 1;
            }
            else i++;
        }
        return list;
    }

    private static string KeyOf(string s, Tok t) =>
        JsonNode.Parse(s[t.Start..t.End]) is JsonValue v && v.TryGetValue(out string? k) ? k : "";

    /// <summary>Insert an entry (preceded by its comment) at the end of the root's "facts" array or "notes" object.</summary>
    internal static string AddToSection(string text, string section, char open, string entry, string comment)
    {
        if (text.Trim().Length == 0) text = "{\n}\n";
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var toks = Lex(text);
        var members = Members(text, toks, out int rootClose);
        foreach (var (key, value) in members)
        {
            if (!string.Equals(KeyOf(text, toks[key]), section, StringComparison.Ordinal)) continue;
            if (toks[value].Ch != open)
                throw new CodeGraphException($"\"{section}\" in the knowledge file is not a{(open == '[' ? " list" : "n object")}.");
            return InsertBefore(text, toks, Matching(toks, value), [comment, entry + ","], nl);
        }
        // no such section yet: add it at the end of the root object
        char close = open == '[' ? ']' : '}';
        return InsertBefore(text, toks, rootClose, [$"\"{section}\": {open}", "  " + comment, "  " + entry + ",", $"{close},"], nl);
    }

    /// <summary>Insert lines before the closing token, indented like the entries around it, adding a comma after the last one.</summary>
    private static string InsertBefore(string s, List<Tok> toks, int close, string[] lines, string nl)
    {
        var c = toks[close];
        var prev = toks[close - 1];
        int lineStart = s.LastIndexOf('\n', Math.Max(0, c.Start - 1)) + 1;
        bool ownLine = s[lineStart..c.Start].Trim().Length == 0;
        string closeIndent = ownLine ? s[lineStart..c.Start] : Indent(s, c.Start);
        string indent = closeIndent + "  ";
        if (prev.Kind != T.Open && Line(s, prev.Start) != Line(s, toks[OpenOf(toks, close)].Start))
            indent = Indent(s, prev.Start).Length > closeIndent.Length ? Indent(s, prev.Start) : indent;
        var block = new StringBuilder();
        foreach (var l in lines) block.Append(indent).Append(l).Append(nl);
        string result = ownLine
            ? s[..lineStart] + block + s[lineStart..]
            : s[..c.Start] + nl + block + closeIndent + s[c.Start..];
        bool comma = prev.Kind is not (T.Open or T.Comma);
        return comma ? result[..prev.End] + "," + result[prev.End..] : result;
    }

    private static int OpenOf(List<Tok> toks, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (toks[i].Kind == T.Close) depth++;
            else if (toks[i].Kind == T.Open && --depth == 0) return i;
        }
        return 0;
    }

    private static int Line(string s, int pos) => s.AsSpan(0, pos).Count('\n');

    private static string Indent(string s, int pos)
    {
        int start = s.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        int i = start;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        return s[start..i];
    }

    /// <summary>Replace an existing note's text (the key stays; the comment goes on the line above it).</summary>
    internal static string ExtendNote(string text, string key, string newText, string comment)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var toks = Lex(text);
        foreach (var (k, v) in Members(text, toks, out _))
        {
            if (KeyOf(text, toks[k]) != "notes" || toks[v].Ch != '{') continue;
            int close = Matching(toks, v);
            for (int i = v + 1; i + 2 < close; i++)
            {
                if (toks[i].Kind != T.String || toks[i + 1].Kind != T.Colon || KeyOf(text, toks[i]) != key) continue;
                var val = toks[i + 2];
                if (val.Kind != T.String) break;
                int lineStart = text.LastIndexOf('\n', Math.Max(0, toks[i].Start - 1)) + 1;
                string withValue = text[..val.Start] + Quote(newText) + text[val.End..];
                return withValue[..lineStart] + Indent(text, toks[i].Start) + comment + nl + withValue[lineStart..];
            }
        }
        throw new CodeGraphException($"Could not find the note \"{key}\" to extend; edit the knowledge file by hand.");
    }

    private static string Quote(string s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JsonOpts.Relaxed }))
            w.WriteStringValue(s);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static JsonObject? Parse(string text, string file)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: JsonOptions) as JsonObject
                   ?? throw new CodeGraphException($"{file}: must hold one {{ ... }} object.");
        }
        catch (JsonException ex)
        {
            throw new CodeGraphException($"{file}: {ex.Message} (nothing was written)");
        }
    }

    private static string Read(string file, out bool bom)
    {
        var bytes = File.ReadAllBytes(file);
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
    }

    private static void Write(string file, string text)
    {
        bool bom = false;
        if (File.Exists(file)) Read(file, out bom);
        else if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(file, text, new UTF8Encoding(bom));
    }
}
