using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeGraph;

public sealed class RepoConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Ignore { get; set; } = [];
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
internal sealed partial class ConfigJson : JsonSerializerContext;
