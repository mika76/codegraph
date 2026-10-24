using System.Text;

namespace CodeGraph.Text;

/// <summary>A string literal found while scrubbing: offset of its opening quote and its unescaped text.</summary>
public readonly record struct StringLit(int Offset, string Value);

/// <summary>
/// Scrubbers blank out comments and string contents (same length as the input, newlines kept, so offsets
/// and line numbers survive) and return the string literals separately.
/// </summary>
public static class Scrub
{
    private static void Blank(StringBuilder sb, string text, int start, int end)
    {
        for (int i = start; i < end; i++) sb.Append(text[i] == '\n' ? '\n' : ' ');
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>C#, C, C++, JavaScript. Handles //, /* */, "..", '..', C# @"..", $@"..", JS `..`.</summary>
    public static (string Code, List<StringLit> Strings) CLike(string text, bool verbatimAt = true, bool backtick = false)
    {
        var sb = new StringBuilder(text.Length);
        var strings = new List<StringLit>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i], nx = At(text, i + 1);
            if (c == '/' && nx == '/')
            {
                int j = text.IndexOf('\n', i);
                if (j < 0) j = n;
                Blank(sb, text, i, j);
                i = j;
            }
            else if (c == '/' && nx == '*')
            {
                int j = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                j = j < 0 ? n : j + 2;
                Blank(sb, text, i, j);
                i = j;
            }
            else if (verbatimAt && (c == '@' && nx == '"' || (c is '$' or '@') && (nx is '$' or '@') && At(text, i + 2) == '"'))
            {
                // C# verbatim string @"..." (doubled quotes escape)
                int start = text.IndexOf('"', i) + 1;
                int j = start;
                while (j < n)
                {
                    if (text[j] == '"')
                    {
                        if (j + 1 < n && text[j + 1] == '"') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                strings.Add(new StringLit(i, text[start..Math.Min(j, n)].Replace("\"\"", "\"")));
                sb.Append(text, i, start - i);
                Blank(sb, text, start, Math.Min(j, n));
                if (j < n) sb.Append('"');
                i = j + 1;
            }
            else if (c == '"' || c == '\'' || backtick && c == '`')
            {
                char q = c;
                int j = i + 1;
                while (j < n && text[j] != q)
                {
                    if (text[j] == '\\') { j += 2; continue; }
                    if (text[j] == '\n' && q != '`') break;
                    j++;
                }
                if (j > n) j = n;
                if (q != '\'' || j - i > 2) // keep char literals out of the string list
                    strings.Add(new StringLit(i, text[(i + 1)..j]));
                sb.Append(q);
                Blank(sb, text, i + 1, j);
                bool closed = j < n && text[j] == q;
                if (closed) sb.Append(q);
                i = closed ? j + 1 : j;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return (sb.ToString(), strings);
    }

    /// <summary>VB6 / VB.NET: ' and Rem comments, "..." strings with "" escapes. Works line by line.</summary>
    public static (string Code, List<StringLit> Strings) Vb(string text)
    {
        var sb = new StringBuilder(text.Length);
        var strings = new List<StringLit>();
        int offset = 0;
        var lines = text.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            int i = 0, n = line.Length;
            while (i < n)
            {
                char c = line[i];
                if (c == '"')
                {
                    int j = i + 1;
                    var buf = new StringBuilder();
                    while (j < n)
                    {
                        if (line[j] == '"')
                        {
                            if (j + 1 < n && line[j + 1] == '"') { buf.Append('"'); j += 2; continue; }
                            break;
                        }
                        buf.Append(line[j]);
                        j++;
                    }
                    strings.Add(new StringLit(offset + i, buf.ToString()));
                    sb.Append('"').Append(' ', Math.Min(j, n) - i - 1);
                    if (j < n) sb.Append('"');
                    i = j + 1;
                }
                else if (c == '\'' || IsRem(line, i))
                {
                    sb.Append(' ', n - i);
                    i = n;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            if (li < lines.Length - 1) sb.Append('\n');
            offset += n + 1;
        }
        return (sb.ToString(), strings);
    }

    private static bool IsRem(string line, int i)
    {
        if (i + 3 > line.Length || string.Compare(line, i, "rem", 0, 3, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (i + 3 < line.Length && char.IsLetterOrDigit(line[i + 3])) return false;
        string before = line[..i].Trim();
        return before is "" or ":";
    }

    /// <summary>PowerShell: # comments, &lt;# #&gt; blocks, '..', "..", @' '@ and @" "@ here-strings.</summary>
    public static (string Code, List<StringLit> Strings) PowerShell(string text)
    {
        var sb = new StringBuilder(text.Length);
        var strings = new List<StringLit>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '<' && At(text, i + 1) == '#')
            {
                int j = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                j = j < 0 ? n : j + 2;
                Blank(sb, text, i, j);
                i = j;
            }
            else if (c == '#')
            {
                int j = text.IndexOf('\n', i);
                if (j < 0) j = n;
                Blank(sb, text, i, j);
                i = j;
            }
            else if (c == '@' && At(text, i + 1) is '\'' or '"' && At(text, i + 2) is '\n' or '\r')
            {
                char q = text[i + 1];
                int end = text.IndexOf("\n" + q + "@", i + 2, StringComparison.Ordinal);
                bool closed = end >= 0;
                if (!closed) end = n;
                strings.Add(new StringLit(i, text[(i + 2)..end].TrimStart('\r', '\n')));
                sb.Append('@').Append(q);
                Blank(sb, text, i + 2, end);
                // keep the closing "\n'@" as code so its quote doesn't open a new string
                if (closed) sb.Append('\n').Append(q).Append('@');
                i = closed ? end + 3 : end;
            }
            else if (c is '\'' or '"')
            {
                char q = c;
                int j = i + 1;
                while (j < n)
                {
                    if (q == '"' && text[j] == '`') { j += 2; continue; }
                    if (text[j] == q)
                    {
                        if (j + 1 < n && text[j + 1] == q) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                if (j > n) j = n;
                strings.Add(new StringLit(i, text[(i + 1)..j]));
                sb.Append(q);
                Blank(sb, text, i + 1, j);
                if (j < n) sb.Append(q);
                i = j + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return (sb.ToString(), strings);
    }

    /// <summary>T-SQL: -- and nested /* */ comments, '..' strings. [bracketed] identifiers are kept as code.</summary>
    public static (string Code, List<StringLit> Strings) Sql(string text)
    {
        var sb = new StringBuilder(text.Length);
        var strings = new List<StringLit>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '-' && At(text, i + 1) == '-')
            {
                int j = text.IndexOf('\n', i);
                if (j < 0) j = n;
                Blank(sb, text, i, j);
                i = j;
            }
            else if (c == '/' && At(text, i + 1) == '*')
            {
                int depth = 1, j = i + 2;
                while (j < n && depth > 0)
                {
                    if (text[j] == '/' && At(text, j + 1) == '*') { depth++; j += 2; }
                    else if (text[j] == '*' && At(text, j + 1) == '/') { depth--; j += 2; }
                    else j++;
                }
                if (j > n) j = n;
                Blank(sb, text, i, j);
                i = j;
            }
            else if (c == '\'')
            {
                int j = i + 1;
                while (j < n)
                {
                    if (text[j] == '\'')
                    {
                        if (j + 1 < n && text[j + 1] == '\'') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                strings.Add(new StringLit(i, text[(i + 1)..j].Replace("''", "'")));
                sb.Append('\'');
                Blank(sb, text, i + 1, j);
                if (j < n) sb.Append('\'');
                i = j + 1;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return (sb.ToString(), strings);
    }

    /// <summary>Index of the bracket that closes the one at <paramref name="openPos"/> (or the last index).</summary>
    public static int MatchBrace(string code, int openPos, char open = '{', char close = '}')
    {
        int depth = 0;
        for (int j = openPos; j < code.Length; j++)
        {
            char ch = code[j];
            if (ch == open) depth++;
            else if (ch == close && --depth == 0) return j;
        }
        return code.Length - 1;
    }

    /// <summary>Brace depth <em>before</em> each character position (length + 1 entries).</summary>
    public static int[] DepthMap(string code)
    {
        var depths = new int[code.Length + 1];
        int d = 0;
        for (int i = 0; i < code.Length; i++)
        {
            depths[i] = d;
            if (code[i] == '{') d++;
            else if (code[i] == '}') d = Math.Max(0, d - 1);
        }
        depths[code.Length] = d;
        return depths;
    }
}
