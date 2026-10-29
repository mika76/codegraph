using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Scan;

/// <summary>
/// Marks "literal" refs (a bare string such as "GetOrders" that may name a stored procedure) whose surrounding code
/// talks to a database: ref.X["db"] = true when the innermost routine (or type) around the string - or the file, for
/// a string outside any - uses a well-known data-access API (ADO, ADO.NET, ODBC, EF raw SQL, sqlcmd, node drivers).
/// The builder links a bare string to a procedure only when it is marked or has a configured proc prefix, so a plain
/// word that happens to be a procedure name ("test") in UI code is not a call. Library APIs only - no assumptions
/// about how the codebase names its own procedures.
/// </summary>
public static partial class DbAccess
{
    [GeneratedRegex(@"\b(?:ADODB|adCmdStoredProc|adCmdText|CommandText|CommandType|StoredProcedure|" +
                    @"OpenRecordset|Recordset|CreateQueryDef|rdoConnection|rdoQuery|rdoResultset|" +
                    @"_CommandPtr|_RecordsetPtr|_ConnectionPtr|" +
                    @"(?:Sql|OleDb|Odbc|Oracle|Npgsql|MySql|Sqlite|SQLite|Db|IDb)(?:Command|DataAdapter|Connection)|" +
                    // ADO.NET, EF Core, EF6 (ExecuteFunction, ExecuteStore*, ExecuteSqlCommand), LINQ to SQL
                    @"Execute(?:Reader|NonQuery|Scalar|SqlRaw|SqlInterpolated|SqlCommand|Function|StoreQuery|" +
                    @"StoreCommand|MethodCall)(?:Async)?|" +
                    @"FromSql(?:Raw|Interpolated)?|SqlQuery|SQLExecDirect|SQLPrepare|" +
                    @"Invoke-Sqlcmd|Invoke-DbaQuery|sqlcmd|osql|isql|" +
                    @"mssql|tedious|oracledb|callproc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Api();

    /// <summary>ADO's conn.Execute "proc" (VB6, C++) and node drivers' request.execute('proc'). Not used for C#,
    /// where Execute() is as often a command pattern as a database call.</summary>
    [GeneratedRegex(@"\.Execute\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecuteMember();

    /// <summary>
    /// A string used to read a column: ADO / DataRow / DataReader accessors (Fields("x"), Item("x"), GetOrdinal("x"),
    /// Field&lt;T&gt;("x"), IsDBNull("x")) or a string indexer (row["x"]). Library APIs only.
    /// </summary>
    [GeneratedRegex("""(?:\b(?:Fields|Item|GetOrdinal|IsDBNull|Field\s*<[^<>()]*>)\s*\(\s*|\[\s*)["']([^"'\r\n]+)["']\s*[\])]""", RegexOptions.IgnoreCase)]
    private static partial Regex ColumnAccess();

    /// <summary>A variable of a row / reader / recordset type: "DataRow row", "IDataReader rd", "rs As ADODB.Recordset".</summary>
    [GeneratedRegex(@"\b(?:DataRow|DataRowView|IDataRecord|IDataReader|\w*DataReader|Recordset)\s+([A-Za-z_]\w*)\b|\b([A-Za-z_]\w*)\s+As\s+(?:New\s+)?(?:\w+\.)?(?:Recordset|DataRow|DataRowView|IDataReader|\w*DataReader)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RowVariable();

    /// <summary>A call with a variable and then a string: Get(row, "x") - the string is a column when the variable is a row.</summary>
    [GeneratedRegex("""\(\s*([A-Za-z_]\w*)\s*,\s*["']([^"'\r\n]+)["']\s*[,)]""")]
    private static partial Regex VariableThenString();

    /// <summary>After a column accessor: it is assigned (rs.Fields("x").Value = 1, row["x"] = 1), not compared.</summary>
    [GeneratedRegex(@"\G(?:\s*\.\s*Value\b)?\s*=(?![=>])", RegexOptions.IgnoreCase)]
    private static partial Regex AssignedAfter();

    /// <summary>Before a column accessor, from the start of its line: only the object it belongs to (a statement, not an If).</summary>
    [GeneratedRegex(@"^\s*(?:(?:Let|Set)\s+)?[\w.!\[\]()]*$", RegexOptions.IgnoreCase)]
    private static partial Regex StatementStart();

    public static void Mark(string fileName, string text, FileFacts facts)
    {
        var literals = facts.Refs.Where(r => r.Kind == "literal").ToList();
        if (literals.Count == 0) return;
        // a column name is no procedure call, whatever its prefix: read through a column accessor, or passed along
        // with a row / reader (your own GetValue(row, "x") helper)
        var textLines = new LineIndex(text);
        var rows = RowVariable().Matches(text)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // a column assigned at the start of a statement is written: data.Fields("x").Value = 1
        var written = ColumnAccess().Matches(text)
            .Where(m => AssignedAfter().IsMatch(text, m.Index + m.Length) &&
                        StatementStart().IsMatch(text[(text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1)..m.Index]))
            .Select(m => (Line: textLines.Line(m.Index), Key: SqlText.LiteralIdentifier(m.Groups[1].Value)))
            .Where(c => c.Key is not null).ToHashSet();
        var columns = ColumnAccess().Matches(text).Select(m => (m.Index, Value: m.Groups[1].Value))
            .Concat(VariableThenString().Matches(text).Where(m => rows.Contains(m.Groups[1].Value)).Select(m => (m.Index, Value: m.Groups[2].Value)))
            .Select(c => (Line: textLines.Line(c.Index), Key: SqlText.LiteralIdentifier(c.Value)))
            .Where(c => c.Key is not null).ToHashSet();
        foreach (var r in literals)
            if (columns.Contains((r.Line, r.Name)))
            {
                r.X["column"] = true;
                if (written.Contains((r.Line, r.Name))) r.Mode = "write";
            }
        (string code, bool execute) = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".frm" or ".bas" or ".cls" or ".ctl" or ".dsr" or ".pag" or ".dob" => (Scrub.Vb(text).Code, true),
            ".vb" => (Scrub.Vb(text).Code, false),
            ".cs" or ".cshtml" or ".razor" => (Scrub.CLike(text).Code, false),
            ".c" or ".cpp" or ".cc" or ".cxx" or ".h" or ".hpp" or ".hxx" or ".inl" => (Scrub.CLike(text, verbatimAt: false).Code, true),
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".mts" or ".cts" or ".vue" =>
                (Scrub.CLike(text, verbatimAt: false, backtick: true).Code, true),
            ".ps1" or ".psm1" => (Scrub.PowerShell(text).Code, false),
            _ => (text, false),
        };
        var li = new LineIndex(code);
        var dbLines = Api().Matches(code).Select(m => li.Line(m.Index))
            .Concat(execute ? ExecuteMember().Matches(code).Select(m => li.Line(m.Index)) : [])
            .ToHashSet();
        if (dbLines.Count == 0) return;
        foreach (var r in literals)
        {
            var scope = facts.Symbols
                .Where(s => s.Lines > 0 && s.Line <= r.Line && r.Line < s.Line + s.Lines)
                .MinBy(s => s.Lines);
            bool db = scope is null || dbLines.Any(l => l >= scope.Line && l < scope.Line + scope.Lines);
            if (db) r.X["db"] = true;
        }
    }
}
