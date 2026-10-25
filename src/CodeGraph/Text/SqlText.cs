using System.Text.RegularExpressions;

namespace CodeGraph.Text;

/// <summary>Finding database object references in SQL text (scripts or SQL embedded in code strings).</summary>
public static partial class SqlText
{
    // an object name with up to 3 dots: [db].[dbo].[Orders], dbo.Orders, #tmp, @tbl
    private const string Name = @"((?:\[[^\]]+\]|[A-Za-z_#@][\w#@$]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_#@][\w#@$]*)){0,3})";
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\binsert\s+(?:into\s+)?" + Name, Opt)] private static partial Regex Insert();
    [GeneratedRegex(@"\bupdate\s+(?:top\s*\(\s*\d+\s*\)\s*)?" + Name + @"\s+set\b", Opt)] private static partial Regex Update();
    [GeneratedRegex(@"\bdelete\s+(?:top\s*\(\s*\d+\s*\)\s*)?(?:from\s+)?" + Name, Opt)] private static partial Regex Delete();
    [GeneratedRegex(@"\bmerge\s+(?:into\s+)?" + Name, Opt)] private static partial Regex Merge();
    [GeneratedRegex(@"\btruncate\s+table\s+" + Name, Opt)] private static partial Regex Truncate();
    [GeneratedRegex(@"\bselect\b[^;]*?\binto\s+" + Name + @"\s+from\b", Opt)] private static partial Regex SelectInto();
    [GeneratedRegex(@"\bfrom\s+" + Name, Opt)] private static partial Regex From();
    [GeneratedRegex(@"\bjoin\s+" + Name, Opt)] private static partial Regex Join();
    [GeneratedRegex(@"\bapply\s+" + Name, Opt)] private static partial Regex Apply();
    [GeneratedRegex(@"\bexec(?:ute)?\s+(?:@\w+\s*=\s*)?" + Name, Opt)] private static partial Regex Exec();
    // schema.function( - any schema; names that are no DB object (x.value( on XML ...) drop out in resolution
    [GeneratedRegex(@"(?<![\w.\]])((?:\[[^\]]+\]|[A-Za-z_]\w*)\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_]\w*))\s*\(", Opt)] private static partial Regex FuncCall();
    [GeneratedRegex(@"\s*\.\s*")] private static partial Regex Dot();
    [GeneratedRegex(@"\b(select|insert|update|delete|exec|execute|merge|truncate)\b", Opt)] private static partial Regex Sqlish();
    [GeneratedRegex(@"^\s*((?:(?:\[[A-Za-z_][^\]]*\]|[A-Za-z_]\w*)\s*\.\s*)?(?:\[[A-Za-z_][^\]]*\]|[A-Za-z_]\w*))\s*$")] private static partial Regex IdentLiteral();

    // UPDATE alias SET ... FROM RealTable alias -> the FROM picks RealTable up as a read; unknown names
    // (the alias) are dropped by the resolver.
    private static readonly (string Mode, Regex[] Patterns)[] Patterns =
    [
        ("write", [Insert(), Update(), Delete(), Merge(), Truncate(), SelectInto()]),
        ("read", [From(), Join(), Apply()]),
        ("exec", [Exec()]),
    ];

    public static readonly HashSet<string> Keywords =
    [
        "select", "where", "set", "values", "on", "as", "with", "nolock", "into", "from", "join", "inner", "left",
        "right", "outer", "cross", "full", "table", "top", "distinct", "and", "or", "not", "null", "case", "when",
        "then", "else", "end", "group", "order", "by", "having", "union", "all", "exists", "in", "is", "like",
        "between", "declare", "begin", "commit", "rollback", "tran", "transaction", "return", "if", "while",
        "sp_executesql", "inserted", "deleted", "output", "openquery", "openrowset", "dual", "lateral",
    ];

    /// <summary>'[dbo].[Orders]' -> 'dbo.orders'; 'Acme.dbo.X' -> 'acme.dbo.x' (caller decides how to match).</summary>
    public static string NormName(string raw)
    {
        var parts = Dot().Split(raw.Trim())
            .Select(p => p.Trim().Trim('[', ']').Trim())
            .Where(p => p.Length > 0);
        return string.Join('.', parts).ToLowerInvariant();
    }

    /// <summary>Strip database/schema: 'acme.dbo.orders' -> 'orders'.</summary>
    public static string ShortName(string name)
    {
        int i = name.LastIndexOf('.');
        return i < 0 ? name : name[(i + 1)..];
    }

    /// <summary>Graph key for a DB object: drop the database and the default 'dbo' schema ('Acme.dbo.X' -> 'x').</summary>
    public static string ObjectKey(string raw)
    {
        var parts = NormName(raw).Split('.');
        if (parts.Length > 2) parts = parts[^2..];
        if (parts.Length == 2 && parts[0] == "dbo") parts = parts[1..];
        return string.Join('.', parts);
    }

    /// <summary>Candidate object references in SQL text as (mode, normalised name); mode is read/write/exec/call.</summary>
    public static HashSet<(string Mode, string Name)> Refs(string sql)
    {
        var found = new HashSet<(string, string)>();
        foreach (var (mode, pats) in Patterns)
            foreach (var p in pats)
                foreach (Match m in p.Matches(sql))
                {
                    string nm = NormName(m.Groups[1].Value);
                    string b = ShortName(nm);
                    if (b.Length == 0 || Keywords.Contains(b) || b.StartsWith('@') || b.StartsWith('#')) continue;
                    if (mode == "exec" && !ExecCallFollows(sql, m.Index + m.Length, b)) continue;
                    found.Add((mode, nm));
                }
        foreach (Match m in FuncCall().Matches(sql))
            found.Add(("call", NormName(m.Groups[1].Value)));
        // old-style joins: FROM Orders o, Lines l - the tables after the first
        foreach (var (name, _, _) in FromListTables(sql).Where(t => t.Index > 0))
            found.Add(("read", name));
        // DELETE FROM x also matched the read FROM pattern -> drop the duplicate read
        var writes = found.Where(f => f.Item1 == "write").Select(f => f.Item2).ToHashSet();
        if (found.Any(f => f.Item1 == "read" && writes.Contains(f.Item2)))
        {
            var reads = CountByShortName(ReadCtx(), sql);
            var deletes = CountByShortName(DeleteCtx(), sql);
            found.RemoveWhere(f => f.Item1 == "read" && writes.Contains(f.Item2) &&
                                   reads.GetValueOrDefault(ShortName(f.Item2)) <= deletes.GetValueOrDefault(ShortName(f.Item2)));
        }
        return found;
    }

    [GeneratedRegex(@"\b(?:from|join)\s+(?:\[?\w+\]?\s*\.\s*)*\[?(\w+)\]?", Opt)] private static partial Regex ReadCtx();
    [GeneratedRegex(@"\bdelete\s+(?:from\s+)?(?:\[?\w+\]?\s*\.\s*)*\[?(\w+)\]?", Opt)] private static partial Regex DeleteCtx();

    /// <summary>How often each (lower-case) object name follows FROM/JOIN or DELETE [FROM] in the text.</summary>
    private static Dictionary<string, int> CountByShortName(Regex re, string sql)
    {
        var counts = new Dictionary<string, int>();
        foreach (Match m in re.Matches(sql))
        {
            string k = m.Groups[1].Value.ToLowerInvariant();
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }
        return counts;
    }

    public static bool LooksLikeSql(string s) => Sqlish().IsMatch(s);

    /// <summary>Parsers join a routine's string pieces with this, so a piece boundary is a line end here.</summary>
    public const string PieceSeparator = "\n";

    private static readonly HashSet<string> AfterExec =
    [
        "null", "default", "with", "go", "select", "insert", "update", "delete", "merge", "exec", "execute", "if",
        "begin", "end", "return", "declare", "set", "print", "commit", "rollback", "create", "alter", "drop", "else",
        "while", "output", "out",
    ];

    /// <summary>
    /// Whether "EXEC name" is a call, judged by what follows the name on its line: nothing (the piece ends), ';',
    /// a parameter (@p, a literal, NULL / DEFAULT), a comment or the next statement. Prose like "Failed to execute
    /// telnet command" or "execute report : x" has an ordinary word or a colon there. A name ending in '_' at the
    /// end of a piece ("EXEC sp_" + name) is cut off by concatenation.
    /// </summary>
    private static bool ExecCallFollows(string sql, int at, string name)
    {
        int eol = sql.IndexOf('\n', at);
        string rest = (eol < 0 ? sql[at..] : sql[at..eol]).TrimStart();
        if (rest.Length == 0) return !name.EndsWith('_');
        char c = rest[0];
        if (c is ';' or '@' or '\'' or ',' or '(' or '[' || char.IsDigit(c) || rest.StartsWith("--") || rest.StartsWith("/*")
            || (c is '-' && rest.Length > 1 && char.IsDigit(rest[1])) || rest.StartsWith("N'", StringComparison.OrdinalIgnoreCase))
            return true;
        int w = 0;
        while (w < rest.Length && (char.IsLetter(rest[w]) || rest[w] == '_')) w++;
        return w > 0 && (w == rest.Length || !char.IsLetterOrDigit(rest[w])) && AfterExec.Contains(rest[..w].ToLowerInvariant());
    }

    /// <summary>
    /// If a string literal is just an object name - 'GetOrders', '[dbo].[x]', 'sales.get_orders' - return its
    /// normalised key ('getorders', 'x', 'sales.get_orders'), else null.
    /// </summary>
    public static string? LiteralIdentifier(string s)
    {
        var m = IdentLiteral().Match(s);
        return m.Success ? ObjectKey(m.Groups[1].Value) : null;
    }
}

/// <summary>HTTP route normalisation shared by controllers (routes) and front ends (endpoint calls).</summary>
public static partial class Routes
{
    [GeneratedRegex(@"^https?://[^/]+")] private static partial Regex Host();
    [GeneratedRegex(@"\$\{[^}]*\}")] private static partial Regex TemplateExpr();
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex RouteParam();
    [GeneratedRegex(@"(?<=^|/):\w+")] private static partial Regex ColonParam();
    [GeneratedRegex(@"/+")] private static partial Regex Slashes();

    /// <summary>'~/api/Orders/{id:int}/?x=1' -> 'api/orders/{}' ; '`/m/${n}/h`' -> 'm/{}/h'.</summary>
    public static string Normalize(string? path)
    {
        string p = (path ?? "").Trim().Trim('`', '\'', '"');
        p = Host().Replace(p, "");
        p = p.Split('?')[0].Split('#')[0];
        p = TemplateExpr().Replace(p, "{}");
        p = RouteParam().Replace(p, "{}");
        p = ColonParam().Replace(p, "{}");
        p = p.TrimStart('~').Trim('/');
        p = Slashes().Replace(p, "/");
        return p.ToLowerInvariant();
    }
}
