namespace CodeGraph.Scan;

/// <summary>
/// gitignore-flavoured matching on forward-slash relative paths, case-insensitive, with '!' negation.
/// A bare name matches any path segment; a pattern with '/' is matched from the repo root; "**/x" anywhere.
/// </summary>
public sealed class IgnoreRules
{
    /// <summary>Copies of well-known libraries: the file itself, or "name-x.js" / "name.x.js" - not "reactions.js".</summary>
    private static readonly string[] VendoredLibs =
        ["jquery", "knockout", "modernizr", "globalize", "bootstrap", "angular", "react", "vue", "lodash", "moment"];

    public static readonly string[] BuiltIn =
    [
        ".git", ".svn", ".vs", ".vscode", ".idea", "$tf", "node_modules", "TestResults",
        "_ReSharper*", "*.Designer.cs", "*.designer.cs", "**/Service References/*/Reference.cs",
        "**/Web References/*/Reference.cs", "**/Connected Services/*/Reference.cs",
        "*.g.cs", "*.g.i.cs", "*.AssemblyInfo.cs", "TemporaryGeneratedFile_*", "*.min.js", "*.debug.js",
        "*-vsdoc.js", "dx.*.js", "cordova.js",
        .. VendoredLibs.SelectMany(l => new[] { $"{l}.js", $"{l}-*.js", $"{l}.*.js" }),
        "graphify-out", "codegraph-out", "wwwroot/lib", "*.d.ts", "dist", ".nuxt", ".output", "coverage",
        "*.bundle.js", "*.chunk.js", "*.map", "Vendor", "vendor", "ThirdParty", "third_party", "* - Backup.*",
        "*.orig", "*.bak",
    ];

    private static readonly string[] ProjectFiles = [".csproj", ".vbproj", ".fsproj", ".vcxproj", ".vcproj", ".dsp", ".sqlproj", ".wixproj"];
    private static readonly string[] NativeBuildFiles = [".vcxproj", ".vcproj", ".dsp", ".dsw", ".sln", ".slnx"];
    private static readonly string[] SolutionFiles = [".sln", ".slnx"];

    /// <summary>
    /// Build output folders are only skipped where a build puts them - bin/obj next to a project file, Debug/Release/
    /// x64/... next to a C++ project or solution, NuGet's packages next to a solution - so a source folder that happens
    /// to be called Release, bin or packages (a JS monorepo) is still read.
    /// </summary>
    public static bool IsBuildOutput(string dirName, IEnumerable<string> siblingFiles)
    {
        string[]? next = dirName.ToLowerInvariant() switch
        {
            "bin" or "obj" => ProjectFiles,
            "debug" or "release" or "x64" or "x86" or "win32" or "arm64" => NativeBuildFiles,
            "packages" => SolutionFiles,
            _ => null,
        };
        return next is not null && siblingFiles.Any(f => next.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
    }

    private readonly List<(bool Negate, string Pattern, bool Anchored)> _rules = [];

    public IgnoreRules(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            string p = (raw ?? "").Trim();
            if (p.Length == 0 || p.StartsWith('#')) continue;
            bool neg = p.StartsWith('!');
            if (neg) p = p[1..];
            p = p.Replace('\\', '/').ToLowerInvariant();
            bool anchored = p.TrimEnd('/').Contains('/');
            _rules.Add((neg, p.Trim('/'), anchored));
        }
    }

    public bool IsIgnored(string rel)
    {
        var parts = rel.Replace('\\', '/').ToLowerInvariant().Split('/');
        int n = parts.Length;
        bool result = false;
        foreach (var (neg, pat, anchored) in _rules)
        {
            bool hit = false;
            if (anchored && pat.StartsWith("**/"))
            {
                string rest = pat[3..];
                for (int i = 0; i < n && !hit; i++)
                    for (int j = i + 1; j <= n && !hit; j++)
                        hit = Glob.Match(string.Join('/', parts[i..j]), rest);
            }
            else if (anchored)
            {
                for (int j = 1; j <= n && !hit; j++)
                    hit = Glob.Match(string.Join('/', parts[..j]), pat);
            }
            else
            {
                foreach (var part in parts)
                    if (Glob.Match(part, pat)) { hit = true; break; }
            }
            if (hit) result = !neg;
        }
        return result;
    }
}

/// <summary>fnmatch-style glob: * (any run, including '/'), ? (one char), [abc] / [!abc] sets. Case-sensitive.</summary>
public static class Glob
{
    public static bool Match(string s, string pattern) => Match(s, 0, pattern, 0);

    private static bool Match(string s, int si, string p, int pi)
    {
        while (pi < p.Length)
        {
            char c = p[pi];
            if (c == '*')
            {
                while (pi < p.Length && p[pi] == '*') pi++;
                if (pi == p.Length) return true;
                for (int k = si; k <= s.Length; k++)
                    if (Match(s, k, p, pi)) return true;
                return false;
            }
            if (si >= s.Length) return false;
            if (c == '?') { si++; pi++; continue; }
            if (c == '[')
            {
                int end = p.IndexOf(']', pi + 2);
                if (end > 0)
                {
                    bool negate = p[pi + 1] is '!' or '^';
                    int start = negate ? pi + 2 : pi + 1;
                    bool inSet = false;
                    for (int k = start; k < end; k++)
                    {
                        if (k + 2 < end && p[k + 1] == '-')
                        {
                            if (s[si] >= p[k] && s[si] <= p[k + 2]) inSet = true;
                            k += 2;
                        }
                        else if (s[si] == p[k]) inSet = true;
                    }
                    if (inSet == negate) return false;
                    si++;
                    pi = end + 1;
                    continue;
                }
            }
            if (s[si] != c) return false;
            si++;
            pi++;
        }
        return si == s.Length;
    }
}
