using System.Text.RegularExpressions;

namespace CodeGraph.Text;

/// <summary>
/// One column a SQL statement may use. <see cref="Table"/> is set when the name was qualified with a table or alias of
/// the statement (o.Total with FROM Orders o); otherwise <see cref="Tables"/> lists the statement's tables and the
/// builder links the name only when exactly one of them has such a column. Mode is read, write or alter.
/// </summary>
public sealed record ColumnUse(string Name, string Mode, string? Table, IReadOnlyList<string> Tables, int Offset);

public static partial class SqlText
{
    [GeneratedRegex(@"[A-Za-z_][\w$#@]*|[();]", Opt)] private static partial Regex StatementToken();
    [GeneratedRegex(@"\b(?:from|join|update|into|apply)\s+" + Name + @"(?:\s+(?:as\s+)?([A-Za-z_]\w*))?", Opt)] private static partial Regex TableAlias();
    [GeneratedRegex(@"\bupdate\s+(?:top\s*\(\s*\d+\s*\)\s*)?" + Name + @"\s+set\b(.*?)(?=\bfrom\b|\bwhere\b|\boutput\b|$)", Opt | RegexOptions.Singleline)] private static partial Regex UpdateSet();
    [GeneratedRegex(@"(?:^|,)\s*(?:(?:\[[^\]]+\]|[A-Za-z_]\w*)\s*\.\s*)?(\[[^\]]+\]|[A-Za-z_]\w*)\s*=", Opt)] private static partial Regex Assignment();
    [GeneratedRegex(@"\binsert\s+(?:into\s+)?" + Name + @"\s*\(([^()]*)\)", Opt)] private static partial Regex InsertColumns();
    [GeneratedRegex(@"\balter\s+table\s+" + Name, Opt)] private static partial Regex AlterTable();
    // a name, optionally qualified (alias.col, table.col, dbo.table.col); not a function call, @variable or #temp
    [GeneratedRegex(@"(?<![\w@#$.\]])((?:(?:\[[^\]]+\]|[A-Za-z_]\w*)\s*\.\s*){0,3})(\[[^\]]+\]|[A-Za-z_]\w*)(?![\w\]])(?!\s*\()", Opt)] private static partial Regex ColumnToken();

    [GeneratedRegex(@"N?'(?:[^']|'')*'", Opt)] private static partial Regex QuotedLiteral();

    private static readonly HashSet<string> DdlWords = new(StringComparer.OrdinalIgnoreCase)
        { "alter", "column", "add", "table", "constraint", "check", "nocheck", "primary", "key", "foreign", "references", "index" };

    [GeneratedRegex(@"\bfrom\s+", Opt)] private static partial Regex FromWord();
    [GeneratedRegex(@"\G\s*" + Name + @"(?:\s+(?:as\s+)?([A-Za-z_]\w*))?", Opt)] private static partial Regex ListedTable();
    [GeneratedRegex(@"\G\s*,", Opt)] private static partial Regex ListComma();

    /// <summary>
    /// The tables of every FROM list, comma joins included (FROM Orders o, Lines l): (table, alias, position in the
    /// list). Stops at anything that is not a plain table (a sub-query, a keyword).
    /// </summary>
    private static IEnumerable<(string Name, string? Alias, int Index)> FromListTables(string sql)
    {
        foreach (Match f in FromWord().Matches(sql))
        {
            int at = f.Index + f.Length;
            for (int i = 0; ; i++)
            {
                var t = ListedTable().Match(sql, at);
                if (!t.Success || Keywords.Contains(t.Groups[1].Value.ToLowerInvariant())) break;
                string name = NormName(t.Groups[1].Value);
                string? alias = t.Groups[2].Success && !NotAliases.Contains(t.Groups[2].Value) && !Keywords.Contains(t.Groups[2].Value.ToLowerInvariant())
                    ? t.Groups[2].Value : null;
                // ListedTable may have swallowed a keyword as alias (FROM t WHERE ...): then the list ends here
                int end = alias is null && t.Groups[2].Success ? t.Groups[2].Index : t.Index + t.Length;
                if (!name.StartsWith('#') && !name.StartsWith('@')) yield return (name, alias, i);
                var comma = ListComma().Match(sql, end);
                if (!comma.Success) break;
                at = comma.Index + comma.Length;
            }
        }
    }

    /// <summary>Words that start a new statement at bracket depth 0 (CASE ... END is kept together).</summary>
    private static readonly HashSet<string> StatementWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // not ALTER / DROP: they also occur inside ALTER TABLE (ALTER COLUMN, DROP COLUMN)
        "select", "insert", "update", "delete", "merge", "declare", "if", "while", "return", "exec", "execute", "begin",
        "end", "else", "print", "raiserror", "truncate", "create", "go", "fetch", "open", "close",
        "deallocate", "commit", "rollback", "throw",
    };

    private static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "on", "set", "inner", "left", "right", "full", "cross", "outer", "join", "with", "group", "order",
        "having", "union", "values", "select", "output", "default", "exec", "execute", "pivot", "unpivot", "for",
    };

    /// <summary>The statements of a SQL text: (start, end) offsets.</summary>
    private static IEnumerable<(int Start, int End)> Statements(string sql)
    {
        int start = 0, depth = 0, cases = 0;
        foreach (Match m in StatementToken().Matches(sql))
        {
            string t = m.Value;
            if (t == "(") { depth++; continue; }
            if (t == ")") { depth = Math.Max(0, depth - 1); continue; }
            if (t == ";")
            {
                if (depth == 0) { yield return (start, m.Index); start = m.Index + 1; }
                continue;
            }
            if (depth > 0) continue;
            if (t.Equals("case", StringComparison.OrdinalIgnoreCase)) { cases++; continue; }
            if (t.Equals("end", StringComparison.OrdinalIgnoreCase) && cases > 0) { cases--; continue; }
            if (StatementWords.Contains(t) && m.Index > start)
            {
                yield return (start, m.Index);
                start = m.Index;
            }
        }
        if (start < sql.Length) yield return (start, sql.Length);
    }

    /// <summary>
    /// Column refs (candidates) for a SQL text, owned like the caller's other refs; <paramref name="lineOf"/> maps an
    /// offset in <paramref name="sql"/> to a line.
    /// </summary>
    public static IEnumerable<Model.Ref> ColumnRefs(string sql, string? from, string? fromParent, Func<int, int> lineOf)
    {
        foreach (var c in Columns(sql))
        {
            var r = new Model.Ref { From = from, FromParent = fromParent, Kind = "column", Name = c.Name, Mode = c.Mode, Line = lineOf(c.Offset) };
            if (c.Table is not null) r.X["table"] = c.Table;
            r.X["tables"] = new System.Text.Json.Nodes.JsonArray(c.Tables.Select(t => (System.Text.Json.Nodes.JsonNode?)t).ToArray());
            r.X["candidate"] = true;
            yield return r;
        }
    }

    /// <summary>
    /// The columns each statement of a SQL text may use, with their tables. Candidates only: a name links in the
    /// builder when it is a column of its (single) table. Definitions (CREATE ...) use no columns.
    /// </summary>
    public static List<ColumnUse> Columns(string sql)
    {
        // 'Closed', N'x': values, not names (same length, so offsets stay valid)
        sql = QuotedLiteral().Replace(sql, m => new string(' ', m.Length));
        var result = new List<ColumnUse>();
        var seen = new HashSet<(string, string, string?)>();
        void Add(string col, string mode, string? table, IReadOnlyList<string> tables, int offset)
        {
            string c = NormName(col);
            if (c.Length == 0 || Keywords.Contains(c) || StatementWords.Contains(c) || NotAliases.Contains(c)
                || DdlWords.Contains(c) || seen.Add((c, mode, table)) is false) return;
            result.Add(new ColumnUse(c, mode, table, tables, offset));
        }
        foreach (var (a, b) in Statements(sql))
        {
            string st = sql[a..b];
            var words = StatementToken().Matches(st).Where(m => char.IsLetter(m.Value[0]) || m.Value[0] == '_').Select(m => m.Value.ToLowerInvariant()).Take(2).ToList();
            if (words.Count == 0 || words[0] == "create" || words[0] == "drop") continue;
            if (words[0] == "alter" && (words.Count < 2 || words[1] != "table")) continue; // ALTER PROC ... header
            var tables = Refs(st).Select(r => r.Name).Where(n => !n.StartsWith('#') && !n.StartsWith('@')).Distinct().ToList();
            // from ALTER TABLE t on (also inside IF NOT EXISTS (...) ALTER TABLE t ADD c int) the names are t's columns
            var altered = AlterTable().Match(st);
            string? alterTable = altered.Success ? NormName(altered.Groups[1].Value) : null;
            if (alterTable is not null && !tables.Contains(alterTable)) tables.Add(alterTable);
            if (tables.Count == 0) continue;
            // aliases and table names usable as qualifiers
            var qualifiers = new Dictionary<string, string>();
            foreach (var t in tables) qualifiers.TryAdd(ShortName(t), t);
            foreach (Match m in TableAlias().Matches(st))
            {
                string t = NormName(m.Groups[1].Value);
                if (m.Groups[2].Success && !NotAliases.Contains(m.Groups[2].Value) && tables.Contains(t))
                    qualifiers[m.Groups[2].Value.ToLowerInvariant()] = t;
            }
            foreach (var (t, alias, _) in FromListTables(st))
                if (alias is not null && tables.Contains(t)) qualifiers[alias.ToLowerInvariant()] = t;
            string? Qualified(string q) => qualifiers.GetValueOrDefault(NormName(q).Split('.')[^1]);
            var used = new HashSet<int>();
            // written: UPDATE t SET col = ..., INSERT INTO t (cols)
            foreach (Match m in UpdateSet().Matches(st))
            {
                string? target = Qualified(m.Groups[1].Value) ?? NormName(m.Groups[1].Value);
                foreach (Match s in Assignment().Matches(m.Groups[2].Value))
                {
                    Add(s.Groups[1].Value, "write", target, tables, a + m.Groups[2].Index + s.Groups[1].Index);
                    used.Add(m.Groups[2].Index + s.Groups[1].Index);
                }
            }
            foreach (Match m in InsertColumns().Matches(st))
            {
                string target = NormName(m.Groups[1].Value);
                int at = m.Groups[2].Index;
                foreach (var part in m.Groups[2].Value.Split(','))
                {
                    int i = at + (part.Length - part.TrimStart().Length);
                    if (part.Trim().Length > 0) { Add(part.Trim(), "write", target, tables, a + i); used.Add(i); }
                    at += part.Length + 1;
                }
            }
            // everything else that names a column of a statement table is read (altered after ALTER TABLE)
            foreach (Match m in ColumnToken().Matches(st))
            {
                int at = m.Groups[2].Index;
                if (used.Contains(at)) continue;
                if (alterTable is not null && at > altered.Index + altered.Length)
                {
                    Add(m.Groups[2].Value, "alter", alterTable, [alterTable], a + at);
                    continue;
                }
                const string mode = "read";
                string q = m.Groups[1].Value.TrimEnd().TrimEnd('.').Trim();
                string col = m.Groups[2].Value;
                if (q.Length == 0)
                {
                    if (qualifiers.ContainsKey(NormName(col))) continue; // a table name or alias itself
                    Add(col, mode, null, tables, a + at);
                }
                else if (Qualified(q) is { } t)
                    Add(col, mode, t, tables, a + at);
            }
        }
        return result;
    }
}
