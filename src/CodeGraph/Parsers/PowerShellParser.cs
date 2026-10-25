using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>Port of codegraph/parsers/powershell.py: PowerShell (.ps1 .psm1) and Windows batch (.bat .cmd).</summary>
public static partial class PowerShellParser
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\bfunction\s+(?:global:|script:)?([\w-]+)\s*(\([^)]*\))?\s*\{", O)]
    private static partial Regex FuncRe();
    [GeneratedRegex(@"(?<![\w$.-])([A-Za-z_][\w-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex CmdRe();
    [GeneratedRegex(@"([\w\-. ]+\.(?:ps1|psm1|bat|cmd|sql|vbp|vbg|csproj|vbproj|vcxproj|sln|exe|dll|ocx|msi|wxs|wixproj))\b", O)]
    private static partial Regex FileMention();
    [GeneratedRegex(@"^(rem\b|::)", O)]
    private static partial Regex RemRe();
    [GeneratedRegex(@"^:(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRe();
    [GeneratedRegex(@"\r\n|[\n\r\v\f\x1c-\x1e\x85  ]", RegexOptions.CultureInvariant)]
    private static partial Regex LineSplit();

    private static readonly HashSet<string> PsKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "elseif", "foreach", "for", "while", "do", "switch", "function", "param", "return",
        "try", "catch", "finally", "throw", "break", "continue", "in", "begin", "process", "end", "exit",
        "trap", "filter", "until", "class", "enum", "using", "data",
    };

    private static List<string> Mentions(string text)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in FileMention().Matches(text))
        {
            string s = m.Groups[1].Value.Trim();
            s = s[(s.LastIndexOf('\\') + 1)..];
            s = s[(s.LastIndexOf('/') + 1)..];
            set.Add(s.ToLowerInvariant());
        }
        return [.. set];
    }

    private static IEnumerable<(string Mode, string Name)> SqlRefsSorted(string sql) =>
        SqlText.Refs(sql).OrderBy(f => f.Mode, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal);

    public static FileFacts Parse(string path, string text)
    {
        if (path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            return ParseBatch(path, text);

        var (code, strings) = Scrub.PowerShell(text);
        var li = new LineIndex(code);
        var facts = new FileFacts { Lang = "powershell" };
        var funcs = new List<(string Name, int A, int B)>();
        foreach (Match m in FuncRe().Matches(code))
        {
            int openPos = m.Index + m.Length - 1;
            int close = Scrub.MatchBrace(code, openPos);
            funcs.Add((m.Groups[1].Value, m.Index, close));
            facts.Symbols.Add(new Symbol
            {
                Name = m.Groups[1].Value, Kind = "function", Line = li.Line(m.Index), Parent = null,
                Visibility = "public", Lines = li.Line(close) - li.Line(m.Index) + 1,
            });
        }

        string? Owner(int pos)
        {
            string? best = null;
            int bestA = -1;
            foreach (var (nm, a, b) in funcs)
                if (a <= pos && pos <= b && (best is null || a > bestA)) { best = nm; bestA = a; }
            return best;
        }

        var seen = new HashSet<(string?, string)>();
        foreach (Match m in CmdRe().Matches(code))
        {
            string w = m.Groups[1].Value;
            if (PsKeywords.Contains(w.ToLowerInvariant())) continue;
            string? o = Owner(m.Index);
            if (!seen.Add((o, w.ToLowerInvariant()))) continue;
            facts.Refs.Add(new Ref { From = o, Kind = "call", Name = w, Line = li.Line(m.Index) });
        }

        var order = new List<string?>();
        var byOwner = new Dictionary<string, List<StringLit>>();
        foreach (var sl in strings)
        {
            string? o = Owner(sl.Offset);
            string key = o ?? "\0";
            if (!byOwner.TryGetValue(key, out var list)) { byOwner[key] = list = []; order.Add(o); }
            list.Add(sl);
        }
        foreach (var o in order)
        {
            var items = byOwner[o ?? "\0"];
            string joined = string.Join(SqlText.PieceSeparator, items.Select(i => i.Value));
            if (SqlText.LooksLikeSql(joined))
                foreach (var (mode, nm) in SqlRefsSorted(joined))
                    facts.Refs.Add(new Ref { From = o, Kind = "sql", Mode = mode, Name = nm, Line = li.Line(items[0].Offset) });
            foreach (var it in items)
            {
                string? lit = SqlText.LiteralIdentifier(it.Value);
                if (lit is not null)
                    facts.Refs.Add(new Ref { From = o, Kind = "literal", Name = lit, Line = li.Line(it.Offset) });
            }
        }
        foreach (var name in Mentions(text))
            facts.Refs.Add(new Ref { From = null, Kind = "mention", Name = name, Line = 1 });
        facts.Loc = SourceText.CountLines(text);
        return facts;
    }

    public static FileFacts ParseBatch(string path, string text)
    {
        var facts = new FileFacts { Lang = "batch" };
        var all = LineSplit().Split(text);
        for (int i = 0; i < all.Length; i++)
        {
            string s = all[i].Trim();
            if (s.Length == 0 || RemRe().IsMatch(s)) continue;
            var m = LabelRe().Match(s);
            if (m.Success)
                facts.Symbols.Add(new Symbol { Name = m.Groups[1].Value, Kind = "label", Line = i + 1, Parent = null, Visibility = "public" });
        }
        foreach (var name in Mentions(text))
            facts.Refs.Add(new Ref { From = null, Kind = "mention", Name = name, Line = 1 });
        if (SqlText.LooksLikeSql(text))
            foreach (var (mode, nm) in SqlRefsSorted(text))
                facts.Refs.Add(new Ref { From = null, Kind = "sql", Mode = mode, Name = nm, Line = 1 });
        facts.Loc = SourceText.CountLines(text);
        return facts;
    }
}
