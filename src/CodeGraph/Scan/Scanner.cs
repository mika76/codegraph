using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Scan;

/// <summary>A source file found in a repo.</summary>
public sealed record SourceFile(string Repo, string Root, string Rel, string Abs, long Size, long MtimeTicks);

/// <summary>A source file with what its parser found.</summary>
public sealed record FileRecord(string Repo, string Root, string Rel, string Abs, FileFacts Facts);

public sealed record ParseError(string Repo, string Rel, string Message);

/// <summary>Walk repos, apply ignore rules, parse files in parallel with a cache keyed by size + mtime.</summary>
public static class Scanner
{
    /// <summary>Bump whenever a parser's output changes, otherwise the stale cache is used.</summary>
    public const int ParserVersion = 2;

    public const string CacheFileName = "cache.json";

    public static List<SourceFile> Discover(Config cfg)
    {
        var output = new List<SourceFile>();
        long maxBytes = cfg.MaxFileKb * 1024L;
        foreach (var repo in cfg.Repos)
        {
            string root = repo.Path;
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"  ! repo folder not found: {root}");
                continue;
            }
            var ignore = new IgnoreRules([
                .. IgnoreRules.BuiltIn, .. cfg.Ignore, .. repo.Ignore,
                .. ReadIgnoreFile(Path.Combine(root, ".codegraphignore")),
                .. ReadIgnoreFile(Path.Combine(root, ".graphifyignore")),
            ]);
            Walk(new DirectoryInfo(root), "");

            void Walk(DirectoryInfo dir, string relDir)
            {
                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
                foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string rel = relDir.Length == 0 ? e.Name : $"{relDir}/{e.Name}";
                    if (e is DirectoryInfo sub)
                    {
                        if (e.Name.StartsWith('.') || ignore.IsIgnored(rel)) continue;
                        if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        Walk(sub, rel);
                    }
                    else if (e is FileInfo f)
                    {
                        if (ParserRegistry.For(f.Name) is null || ignore.IsIgnored(rel)) continue;
                        if (f.Length > maxBytes) continue;
                        output.Add(new SourceFile(repo.Name, root, rel, f.FullName, f.Length, f.LastWriteTimeUtc.Ticks));
                    }
                }
            }
        }
        return output;
    }

    private static IEnumerable<string> ReadIgnoreFile(string path)
    {
        try { return File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static FileFacts ParseOne(string absPath, string rel, out string? error)
    {
        error = null;
        try
        {
            string text = SourceText.Read(absPath);
            return ParserRegistry.For(Path.GetFileName(absPath))!(rel, text);
        }
        catch (Exception ex) // keep going on odd files
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return new FileFacts { Lang = "unknown", Error = error };
        }
    }

    public static (List<FileRecord> Records, List<ParseError> Errors) ParseAll(Config cfg, List<SourceFile> files, bool verbose = true)
    {
        string cachePath = Path.Combine(cfg.Output, CacheFileName);
        var cache = LoadCache(cachePath);
        var results = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        var todo = new List<SourceFile>();
        foreach (var f in files)
        {
            if (cache.TryGetValue(f.Abs, out var hit) && hit.Size == f.Size && hit.Mtime == f.MtimeTicks)
                results[f.Abs] = hit;
            else
                todo.Add(f);
        }
        if (verbose)
            Console.WriteLine($"  {files.Count} files found, {files.Count - todo.Count} unchanged (cached), {todo.Count} to parse");

        var errors = new ConcurrentBag<ParseError>();
        var sw = Stopwatch.StartNew();
        var options = new ParallelOptions { MaxDegreeOfParallelism = cfg.Jobs > 0 ? cfg.Jobs : Environment.ProcessorCount };
        Parallel.ForEach(todo, options, f =>
        {
            var facts = ParseOne(f.Abs, f.Rel, out var err);
            results[f.Abs] = new CacheEntry { Size = f.Size, Mtime = f.MtimeTicks, Facts = facts };
            if (err is not null) errors.Add(new ParseError(f.Repo, f.Rel, err));
        });
        if (verbose && todo.Count > 0)
            Console.WriteLine($"  parsed in {sw.Elapsed.TotalSeconds:0.0}s");

        Directory.CreateDirectory(cfg.Output);
        var data = new CacheFile { Version = ParserVersion, Files = new(results, StringComparer.OrdinalIgnoreCase) };
        using (var fs = File.Create(cachePath))
            JsonSerializer.Serialize(fs, data, CacheJson.Default.CacheFile);

        var records = files.Select(f => new FileRecord(f.Repo, f.Root, f.Rel, f.Abs, results[f.Abs].Facts)).ToList();
        return (records, errors.OrderBy(e => e.Repo).ThenBy(e => e.Rel, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static Dictionary<string, CacheEntry> LoadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var fs = File.OpenRead(path);
            var data = JsonSerializer.Deserialize(fs, CacheJson.Default.CacheFile);
            if (data?.Version == ParserVersion)
                return new Dictionary<string, CacheEntry>(data.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return [];
    }
}

public sealed class CacheEntry
{
    public long Size { get; set; }
    public long Mtime { get; set; }
    public FileFacts Facts { get; set; } = new();
}

public sealed class CacheFile
{
    public int Version { get; set; }
    public Dictionary<string, CacheEntry> Files { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CacheFile))]
internal sealed partial class CacheJson : JsonSerializerContext;
