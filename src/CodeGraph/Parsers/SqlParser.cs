using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>T-SQL files: object definitions (tables, views, procedures, functions, triggers, types) and their references.
/// Port of codegraph/parsers/sql.py.</summary>
public static partial class SqlParser
{
    private const string NameRx = @"((?:\[[^\]]+\]|[A-Za-z_#][\w#$]*)(?:\s*\.\s*(?:\[[^\]]+\]|[A-Za-z_#][\w#$]*)){0,2})";
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"\b(create(?:\s+or\s+alter)?|alter)\s+(procedure|proc|function|view|table|trigger|type|synonym)\s+" + NameRx, Ic)]
    private static partial Regex DefRe();
    [GeneratedRegex(@"\bforeign\s+key\s*\([^)]*\)\s*references\s+" + NameRx, Ic)]
    private static partial Regex FkRe();
    [GeneratedRegex(@"^\s*on\s+" + NameRx, Ic)]
    private static partial Regex TriggerOnRe();
    [GeneratedRegex(@"^\s*,?\s*\[?([A-Za-z_][\w ]*?)\]?\s+\[?(\w+)\]?\s*(\([^)]*\))?", Ic | RegexOptions.Multiline)]
    private static partial Regex ColumnRe();

    /// <summary>The parts of a list split at commas outside brackets: "a int, b decimal(10, 2)" -> 2 parts.</summary>
    private static IEnumerable<string> TopLevelParts(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return list[start..i];
                start = i + 1;
            }
        }
        if (start < list.Length) yield return list[start..];
    }
    [GeneratedRegex(@"\bfor\s+" + NameRx, Ic)]
    private static partial Regex SynonymForRe();
    [GeneratedRegex(@"\s*\.\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DotRe();

    private static string Kind(string typ) => typ switch
    {
        "procedure" or "proc" => "db_proc",
        "function" => "db_function",
        "view" => "db_view",
        "table" => "db_table",
        "trigger" => "db_trigger",
        "type" => "db_type",
        _ => "db_synonym",
    };

    private static readonly HashSet<string> NotColumns = ["constraint", "primary", "foreign", "index", "unique", "check"];

    /// <summary>Normalized graph key for a DB object: drop database and the default 'dbo' schema.</summary>
    public static string ObjectKey(string raw) => SqlText.ObjectKey(raw);

    private static string Display(string raw)
    {
        var parts = DotRe().Split(raw.Trim()).Select(p => p.Trim().Trim('[', ']')).ToList();
        if (parts.Count > 2) parts = parts[^2..];
        if (parts.Count == 2 && parts[0].Equals("dbo", StringComparison.OrdinalIgnoreCase)) parts = parts[1..];
        return string.Join('.', parts);
    }

    private static Ref MakeRef(string? from, string kind, string name, int line, string? mode = null) =>
        new() { From = from, Kind = kind, Name = name, Line = line, Mode = mode };

    public static FileFacts Parse(string path, string text)
    {
        var (code, strings) = Scrub.Sql(text);
        var li = new LineIndex(code);
        // CREATE TABLE #tmp inside a procedure is not an object (and must not cut the procedure's body short)
        var defs = DefRe().Matches(code).Where(m => !m.Groups[3].Value.TrimStart('[').StartsWith('#')).ToList();
        var symbols = new List<Symbol>();
        var refs = new List<Ref>();

        // segment boundaries: each definition runs to the next definition
        var spans = new List<(Match M, int End)>();
        for (int i = 0; i < defs.Count; i++)
            spans.Add((defs[i], i + 1 < defs.Count ? defs[i + 1].Index : code.Length));

        string StringsIn(int a, int b) =>
            string.Join(' ', strings.Where(s => a <= s.Offset && s.Offset < b).Select(s => s.Value));

        var claimed = new List<(int, int)>();
        var defined = new HashSet<string>();
        foreach (var (m, end) in spans)
        {
            string verb = m.Groups[1].Value.ToLowerInvariant(), typ = m.Groups[2].Value.ToLowerInvariant();
            string raw = m.Groups[3].Value;
            string kind = Kind(typ);
            string key = ObjectKey(raw);
            int line = li.Line(m.Index);
            int bodyStart = m.Index + m.Length;
            string body = code[bodyStart..end];
            if (verb == "alter" && typ == "table")
            {
                // ALTER TABLE x ADD CONSTRAINT ... : not a definition, a schema change / FK
                foreach (Match fk in FkRe().Matches(body))
                {
                    var r = MakeRef(null, "fk", ObjectKey(fk.Groups[1].Value), li.Line(bodyStart + fk.Index));
                    r.X["owner_hint"] = key;
                    refs.Add(r);
                }
                refs.Add(MakeRef(null, "sql", key, line, "alter"));
                continue;
            }
            claimed.Add((m.Index, end));
            var sym = new Symbol
            {
                Name = Display(raw), Kind = kind, Line = line, Parent = null, Visibility = "public",
                Lines = li.Line(end) - line + 1,
            };
            sym.X["key"] = key;
            sym.X["verb"] = verb;
            if (kind == "db_table")
            {
                int paren = body.IndexOf('(');
                if (paren >= 0)
                {
                    int depth = 0, j = paren;
                    while (j < body.Length)
                    {
                        if (body[j] == '(') depth++;
                        else if (body[j] == ')')
                        {
                            depth--;
                            if (depth == 0) break;
                        }
                        j++;
                    }
                    var cols = new JsonArray();
                    int count = 0;
                    // one definition per top-level comma, whether written one per line or all on one line
                    foreach (string part in TopLevelParts(body[(paren + 1)..Math.Min(j, body.Length)]))
                    {
                        var cm = ColumnRe().Match(part);
                        if (!cm.Success) continue;
                        string cname = cm.Groups[1].Value.Trim();
                        if (NotColumns.Contains(cname.ToLowerInvariant())) continue;
                        if (count++ >= 300) break;
                        cols.Add((JsonNode?)JsonValue.Create($"{cname} {cm.Groups[2].Value}{cm.Groups[3].Value}".Trim()));
                    }
                    sym.X["columns"] = cols;
                }
                foreach (Match fk in FkRe().Matches(body))
                    refs.Add(MakeRef(key, "fk", ObjectKey(fk.Groups[1].Value), li.Line(bodyStart + fk.Index)));
            }
            else if (kind == "db_trigger")
            {
                var tm = TriggerOnRe().Match(body);
                if (tm.Success) refs.Add(MakeRef(key, "trigger_on", ObjectKey(tm.Groups[1].Value), line));
            }
            else if (kind == "db_synonym")
            {
                var sm = SynonymForRe().Match(body);
                if (sm.Success) refs.Add(MakeRef(key, "sql", ObjectKey(sm.Groups[1].Value), line, "read"));
            }
            if (kind is "db_proc" or "db_function" or "db_view" or "db_trigger")
            {
                var seen = new HashSet<(string, string)>();
                foreach (var (mode, nm) in Sorted(SqlText.Refs(body)))
                {
                    var k2 = (mode, ObjectKey(nm));
                    if (k2.Item2 != key && seen.Add(k2))
                        refs.Add(MakeRef(key, "sql", k2.Item2, line, mode));
                }
                refs.AddRange(SqlText.ColumnRefs(body, key, null, o => li.Line(bodyStart + o)));
                string dyn = StringsIn(m.Index, end);
                if (SqlText.LooksLikeSql(dyn))
                {
                    foreach (var (mode, nm) in Sorted(SqlText.Refs(dyn)))
                    {
                        var k2 = (mode, ObjectKey(nm));
                        if (k2.Item2 != key && seen.Add(k2))
                        {
                            var r = MakeRef(key, "sql", k2.Item2, line, mode);
                            r.X["dynamic"] = true;
                            refs.Add(r);
                        }
                    }
                    // SET @sql = 'SELECT t.Col ...'; exec(@sql): its columns too, like its tables
                    foreach (var c in SqlText.ColumnRefs(dyn, key, null, _ => line))
                    {
                        c.X["dynamic"] = true;
                        refs.Add(c);
                    }
                }
            }
            symbols.Add(sym);
            defined.Add(key);
        }

        // ALTER TABLE in the same file as the CREATE TABLE belongs to that table definition
        var fixedRefs = new List<Ref>();
        foreach (var r in refs)
        {
            if (r.Extra is { } ex && ex["owner_hint"] is JsonValue ov && ov.TryGetValue<string>(out var hint) && defined.Contains(hint))
            {
                r.From = hint;
                ex.Remove("owner_hint");
                if (ex.Count == 0) r.Extra = null;
            }
            if (r.From is null && r.Mode == "alter" && defined.Contains(r.Name)) continue;
            fixedRefs.Add(r);
        }
        refs = fixedRefs;

        // statements outside any definition (patch / data scripts)
        var rest = new StringBuilder(code);
        foreach (var (a, b) in claimed)
            for (int k = a; k < b && k < rest.Length; k++) rest[k] = ' ';
        var seenRest = new HashSet<(string, string)>();
        string restText = rest.ToString();
        foreach (var (mode, nm) in Sorted(SqlText.Refs(restText)))
        {
            var k2 = (mode, ObjectKey(nm));
            if (seenRest.Add(k2)) refs.Add(MakeRef(null, "sql", k2.Item2, 1, mode));
        }
        // an ALTER TABLE of a table defined in this file is part of its definition, as above
        refs.AddRange(SqlText.ColumnRefs(restText, null, null, o => li.Line(o)).Where(r =>
            r.Mode != "alter" || r.Extra?["tables"] is not System.Text.Json.Nodes.JsonArray { Count: 1 } t
            || !defined.Contains(ObjectKey(t[0]?.ToString() ?? ""))));

        return new FileFacts { Lang = "sql", Symbols = symbols, Refs = refs, Loc = SourceText.CountLines(text) };
    }

    // Python iterated a set (arbitrary order); sort so the output is deterministic.
    private static IEnumerable<(string Mode, string Name)> Sorted(HashSet<(string Mode, string Name)> set) =>
        set.OrderBy(t => t.Mode, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal);
}
