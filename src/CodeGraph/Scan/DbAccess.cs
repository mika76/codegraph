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
                    @"Execute(?:Reader|NonQuery|Scalar|SqlRaw|SqlInterpolated)(?:Async)?|" +
                    @"FromSql(?:Raw|Interpolated)?|SqlQuery|SQLExecDirect|SQLPrepare|" +
                    @"Invoke-Sqlcmd|Invoke-DbaQuery|sqlcmd|osql|isql|" +
                    @"mssql|tedious|oracledb|callproc)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Api();

    /// <summary>ADO's conn.Execute "proc" (VB6, C++) and node drivers' request.execute('proc'). Not used for C#,
    /// where Execute() is as often a command pattern as a database call.</summary>
    [GeneratedRegex(@"\.Execute\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExecuteMember();

    public static void Mark(string fileName, string text, FileFacts facts)
    {
        var literals = facts.Refs.Where(r => r.Kind == "literal").ToList();
        if (literals.Count == 0) return;
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
