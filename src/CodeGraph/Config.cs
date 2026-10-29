using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeGraph;

public sealed class RepoConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Ignore { get; set; } = [];
    /// <summary>The database the SQL scripts of this repo create (shown on its db: and col: nodes).</summary>
    public string? Database { get; set; }
    /// <summary>Folder (repo-relative) -> database, for repos with scripts of several databases; the deepest folder wins.</summary>
    public Dictionary<string, string> Databases { get; set; } = [];

    /// <summary>The database of a repo-relative file: its deepest folder in <see cref="Databases"/>, else <see cref="Database"/>.</summary>
    public string? DatabaseOf(string rel)
    {
        string key = Build.Paths.Key(rel);
        string? best = null;
        int bestLen = -1;
        foreach (var (folder, db) in Databases)
        {
            string fk = Build.Paths.Key(folder);
            if (fk == "." || key.StartsWith(fk + "/", StringComparison.Ordinal))
                if ((fk == "." ? 0 : fk.Length) > bestLen) (best, bestLen) = (db, fk == "." ? 0 : fk.Length);
        }
        return best ?? Database;
    }
}

/// <summary>Rules that turn one of your own XML/JSON file formats into graph nodes and links (docs/design/extractors.md).</summary>
public sealed class ExtractorConfig
{
    public string Name { get; set; } = "";
    /// <summary>Patterns like the ignore rules: a bare name matches anywhere, a path with '/' from the repo root.</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>Only these repos (default: all).</summary>
    public List<string> Repos { get; set; } = [];
    /// <summary>"xml" or "json" (default: from the file extension, xml unless .json).</summary>
    public string? Format { get; set; }
    /// <summary>Path selecting one item per match (default: the document root).</summary>
    public string? Items { get; set; }
    public ExtractorNode Node { get; set; } = new();
    public List<ExtractorLink> Links { get; set; } = [];
    /// <summary>XML namespace prefixes for the paths; without it, element names match ignoring namespaces.</summary>
    public Dictionary<string, string> Namespaces { get; set; } = [];
    /// <summary>Link code string literals equal to "name" and/or "uses_file" values to the item (relation "opens").</summary>
    public List<string> ReferencedBy { get; set; } = [];
}

public sealed class ExtractorNode
{
    public string Kind { get; set; } = "";
    /// <summary>Path (relative to the item) of the node's name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Display attributes: label -> path.</summary>
    public Dictionary<string, string> Attrs { get; set; } = [];
    /// <summary>Repeated entries shown on the note, e.g. the rows of a lookup table.</summary>
    public List<ExtractorList> Lists { get; set; } = [];
}

public sealed class ExtractorList
{
    public string Label { get; set; } = "";
    /// <summary>Path (relative to the item) of the entries, e.g. "Data/Row".</summary>
    public string Items { get; set; } = "";
    /// <summary>Path (relative to an entry) of its fields; each becomes a column named after the attribute/element (default: all attributes).</summary>
    public string Values { get; set; } = "@*";
}

public sealed class ExtractorLink
{
    public string? Relation { get; set; }
    /// <summary>Path (relative to the item) of the linked names; each value found gives one link.</summary>
    public string? Value { get; set; }
    /// <summary>auto | db | project | file | symbol | release | new.</summary>
    public string To { get; set; } = "auto";
    /// <summary>
    /// Optional regex each value must match (others are skipped); with <see cref="Replace"/> it also rewrites the value
    /// (e.g. "^(\\d\\d)(\\d\\d)(\\d\\d)$" -> "$1.$2.$3").
    /// </summary>
    public string? Pattern { get; set; }
    public string? Replace { get; set; }
    /// <summary>Kind of the node to create when nothing existing matches (e.g. "parameter").</summary>
    public string? Kind { get; set; }
    /// <summary>Path of text to parse as T-SQL instead of a link.</summary>
    public string? Sql { get; set; }
}

public sealed class ObsidianConfig
{
    /// <summary>Sub-folder inside the vault for generated notes ("" = vault root).</summary>
    public string Folder { get; set; } = "";
    public int MaxRoutinesPerNote { get; set; } = 400;
    public bool GraphColors { get; set; } = true;
}

/// <summary>codegraph.json. Paths may be absolute or relative to the config file.</summary>
public sealed class Config
{
    public string Output { get; set; } = "codegraph-out";
    public string? Vault { get; set; }
    public List<RepoConfig> Repos { get; set; } = [];
    public List<string> Ignore { get; set; } = [];
    public string? Releases { get; set; }
    public bool ReleasesFromBranches { get; set; } = true;
    public List<string> Knowledge { get; set; } = [];
    public int MaxFileKb { get; set; } = 4096;
    /// <summary>Parallel parse workers; 0 = one per CPU.</summary>
    public int Jobs { get; set; }
    public List<string> ExternalIgnore { get; set; } =
    [
        "System", "System.*", "Microsoft.CSharp", "Microsoft.VisualBasic", "mscorlib", "netstandard",
        "WindowsBase", "PresentationCore", "PresentationFramework", "stdole", "stdole2.tlb", "OLE Automation",
        "msvbvm60.dll", "Visual Basic runtime objects and procedures", "Visual Basic objects and procedures",
        "Microsoft.VisualStudio.*", "MSTest.*", "Microsoft.NET.Test.Sdk", "kernel32.lib", "user32.lib",
        "gdi32.lib", "winspool.lib", "comdlg32.lib", "advapi32.lib", "shell32.lib", "ole32.lib",
        "oleaut32.lib", "uuid.lib", "odbc32.lib", "odbccp32.lib",
    ];
    /// <summary>
    /// Name prefixes of your stored procedures (without the trailing '_'). A string literal like "usp_Foo" that
    /// matches no DB object is then listed in the report as a procedure called from code but not found.
    /// </summary>
    public List<string> ProcPrefixes { get; set; } = ["sp", "usp", "proc"];
    /// <summary>Added to <see cref="ExternalIgnore"/> instead of replacing it.</summary>
    public List<string> ExternalIgnoreExtra { get; set; } = [];
    public ObsidianConfig Obsidian { get; set; } = new();
    public List<ExtractorConfig> Extractors { get; set; } = [];

    [JsonIgnore] public string ConfigDir { get; set; } = "";

    public static Config Load(string path)
    {
        Config cfg;
        try
        {
            cfg = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.Config) ?? new Config();
        }
        catch (JsonException ex)
        {
            throw new CodeGraphException($"{path}: {ex.Message}");
        }
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        cfg.ConfigDir = dir;
        cfg.Output = Resolve(dir, cfg.Output)!;
        cfg.Vault = Resolve(dir, cfg.Vault);
        cfg.Releases = Resolve(dir, cfg.Releases);
        cfg.Knowledge = cfg.Knowledge.Select(k => Resolve(dir, k)!).ToList();
        cfg.ExternalIgnore.AddRange(cfg.ExternalIgnoreExtra);
        foreach (var r in cfg.Repos)
        {
            r.Path = Resolve(dir, r.Path)!;
            if (string.IsNullOrEmpty(r.Name))
                r.Name = System.IO.Path.GetFileName(r.Path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : r.Path;
        }
        foreach (var x in cfg.Extractors)
        {
            string label = string.IsNullOrEmpty(x.Name) ? "(unnamed)" : x.Name;
            if (x.Files.Count == 0 || string.IsNullOrEmpty(x.Node.Kind) || string.IsNullOrEmpty(x.Node.Name))
                throw new CodeGraphException($"Extractor {label}: \"files\", \"node.kind\" and \"node.name\" are required.");
            if (x.Node.Kind.StartsWith("db_", StringComparison.OrdinalIgnoreCase) ||
                x.Links.Any(l => l.Kind?.StartsWith("db_", StringComparison.OrdinalIgnoreCase) == true))
                throw new CodeGraphException($"Extractor {label}: kinds starting with \"db_\" are reserved for database objects.");
            if (string.IsNullOrEmpty(x.Name)) x.Name = x.Node.Kind;
            foreach (var l in x.Links)
                if (string.IsNullOrEmpty(l.Sql) && (string.IsNullOrEmpty(l.Value) || string.IsNullOrEmpty(l.Relation)))
                    throw new CodeGraphException($"Extractor {label}: each link needs \"relation\" and \"value\" (or \"sql\").");
        }
        var dupes = cfg.Repos.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();
        if (dupes.Count > 0)
            throw new CodeGraphException($"Repo names must be unique, duplicated: {string.Join(", ", dupes)}. " +
                                         "Give them a \"name\" in the config.");
        return cfg;
    }

    private static string? Resolve(string baseDir, string? p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        p = Environment.ExpandEnvironmentVariables(p);
        if (p.StartsWith('~'))
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(baseDir, p));
    }
}

/// <summary>An error to show the user without a stack trace.</summary>
public sealed class CodeGraphException(string message) : Exception(message);

[JsonSourceGenerationOptions(
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Config))]
[JsonSerializable(typeof(ExtractorConfig))]
internal sealed partial class ConfigJson : JsonSerializerContext;
