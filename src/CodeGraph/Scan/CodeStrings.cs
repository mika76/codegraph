using System.Text.Json.Nodes;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Scan;

/// <summary>
/// Keeps the short string literals of a code file (facts.X["strings"] = [[line, text], ...]) so the builder can link
/// code that names one of your extractor items - e.g. a screen calling ShowReport("Sales by Period").
/// </summary>
public static class CodeStrings
{
    private const int MinLength = 3, MaxLength = 200;

    public static void Add(string fileName, string text, FileFacts facts)
    {
        List<StringLit>? strings = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".frm" or ".bas" or ".cls" or ".ctl" or ".dsr" or ".pag" or ".dob" or ".vb" => Scrub.Vb(text).Strings,
            ".cs" or ".cshtml" or ".razor" => Scrub.CLike(text).Strings,
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" or ".hxx" or ".inl" => Scrub.CLike(text, verbatimAt: false).Strings,
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".mts" or ".cts" or ".vue" =>
                Scrub.CLike(text, verbatimAt: false, backtick: true).Strings,
            ".ps1" or ".psm1" => Scrub.PowerShell(text).Strings,
            _ => null,
        };
        if (strings is null || strings.Count == 0) return;
        var li = new LineIndex(text);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new JsonArray();
        foreach (var s in strings)
        {
            string v = s.Value.Trim();
            if (v.Length < MinLength || v.Length > MaxLength || v.Contains('\n') || SqlText.LooksLikeSql(v)) continue;
            if (!seen.Add(v)) continue;
            list.Add((JsonNode)new JsonArray(li.Line(s.Offset), v));
        }
        if (list.Count > 0) facts.X["strings"] = list;
    }
}
