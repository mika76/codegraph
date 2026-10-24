using CodeGraph.Model;
using CodeGraph.Parsers;

namespace CodeGraph.Scan;

public delegate FileFacts ParseFn(string relPath, string text);

/// <summary>Picks the parser for a file by extension or file name.</summary>
public static class ParserRegistry
{
    public static readonly HashSet<string> ProjectExts = new(StringComparer.OrdinalIgnoreCase)
        { ".vbp", ".csproj", ".vbproj", ".vcxproj", ".vcproj", ".dsp", ".sqlproj", ".fsproj", ".wixproj" };
    public static readonly HashSet<string> SolutionExts = new(StringComparer.OrdinalIgnoreCase) { ".sln", ".slnx", ".vbg" };

    private static readonly Dictionary<string, ParseFn> ByExt = Build();

    // matched by file name rather than extension
    private static readonly Dictionary<string, ParseFn> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["package.json"] = JavaScriptParser.Parse,
        ["tsconfig.json"] = JavaScriptParser.Parse,
        ["jsconfig.json"] = JavaScriptParser.Parse,
    };

    private static Dictionary<string, ParseFn> Build()
    {
        var d = new Dictionary<string, ParseFn>(StringComparer.OrdinalIgnoreCase);
        void Add(ParseFn fn, params string[] exts) { foreach (var e in exts) d[e] = fn; }
        Add(Vb6Parser.Parse, ".frm", ".bas", ".cls", ".ctl", ".dsr", ".pag", ".dob", ".vb");
        Add(CSharpParser.Parse, ".cs");
        Add(CppParser.Parse, ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx", ".inl", ".def");
        Add(SqlParser.Parse, ".sql");
        Add(PowerShellParser.Parse, ".ps1", ".psm1", ".bat", ".cmd");
        Add(JavaScriptParser.Parse, ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".mts", ".cts", ".vue");
        Add(RustParser.Parse, ".rs");
        Add(RazorParser.Parse, ".cshtml", ".razor", ".vbhtml");
        Add(WixParser.Parse, ".wxs", ".wxi");
        Add(ProjectParser.Parse, [.. ProjectExts, .. SolutionExts]);
        return d;
    }

    public static ParseFn? For(string fileName)
    {
        if (ByName.TryGetValue(fileName, out var fn)) return fn;
        if (fileName.StartsWith("tsconfig.", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return JavaScriptParser.Parse;
        return ByExt.GetValueOrDefault(Path.GetExtension(fileName));
    }
}
