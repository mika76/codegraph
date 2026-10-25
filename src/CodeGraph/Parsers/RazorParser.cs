using System.Text;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>ASP.NET Razor views (.cshtml / .razor): embedded &lt;script&gt; JavaScript plus MVC wiring (model, actions,
/// partials). Port of codegraph/parsers/razor.py.</summary>
public static partial class RazorParser
{
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions Cv = RegexOptions.CultureInvariant;

    /// <summary>Parses the JavaScript inside &lt;script&gt; blocks: (path, text, lang) -> facts (tests inject their own).</summary>
    public static Func<string, string, string, FileFacts> ScriptParser { get; set; } =
        static (path, text, lang) => JavaScriptParser.ParseScript(path, text, lang);

    private static readonly HashSet<string> CsPrimitives =
    [
        "string", "int", "long", "short", "byte", "bool", "decimal", "double", "float", "object", "char", "dynamic",
        "var", "uint", "ulong", "ushort", "sbyte", "new", "global",
    ];

    private static readonly HashSet<string> RazorKeywords =
    [
        "if", "foreach", "for", "while", "switch", "using", "lock", "section", "functions", "code", "try", "else", "do",
        "model", "inherits", "inject", "page", "layout", "namespace", "implements", "attribute",
    ];

    [GeneratedRegex(@"<script\b([^>]*)>(.*?)</script\s*>", Ic | RegexOptions.Singleline)] private static partial Regex ScriptRe();
    [GeneratedRegex(@"^[ \t]*@(model|inherits)[ \t]+([^\r\n]+)", Cv | RegexOptions.Multiline)] private static partial Regex DirectiveRe();
    [GeneratedRegex(@"\bUrl\.Action\s*\(", Cv)] private static partial Regex UrlAction();
    // what precedes @Url.Action(...) when it is a link: href="  or  location = '  /  location.href = "
    [GeneratedRegex(@"(?:\bhref\s*=\s*[""']?|\blocation(?:\.href)?\s*=\s*[""']?|\.attr\(\s*[""']href[""']\s*,\s*[""']?)\s*@?$", Cv | RegexOptions.IgnoreCase)]
    private static partial Regex NavigationBefore();
    [GeneratedRegex(@"^<a\b", Cv | RegexOptions.IgnoreCase)] private static partial Regex AnchorTag();
    [GeneratedRegex(@"\b(?:Html|Ajax)\.ActionLink\s*\(", Cv)] private static partial Regex ActionLink();
    [GeneratedRegex(@"\b(?:Html|Ajax)\.BeginForm\s*\(", Cv)] private static partial Regex BeginForm();
    [GeneratedRegex(@"\bHtml\.(?:Render)?Action\s*\(", Cv)] private static partial Regex RenderAction();
    [GeneratedRegex("\\bHtml\\.(?:Render)?Partial(?:Async)?\\s*\\(\\s*@?\"([^\"]+)\"", Cv)] private static partial Regex PartialCall();
    [GeneratedRegex("<partial\\b[^>]*?\\bname\\s*=\\s*[\"']([^\"']+)[\"']", Ic)] private static partial Regex PartialTag();
    [GeneratedRegex(@"<[A-Za-z][\w:-]*\b[^>]*\basp-(?:action|controller)\s*=[^>]*>", Cv)] private static partial Regex TagHelper();
    [GeneratedRegex(@"(?:^|[\\/])Views[\\/]([^\\/]+)[\\/][^\\/]+$", Ic)] private static partial Regex ViewPath();
    [GeneratedRegex(@"@\*.*?\*@", Cv | RegexOptions.Singleline)] private static partial Regex RazorComment();
    [GeneratedRegex(@"[^\n]", Cv)] private static partial Regex NotNewline();
    [GeneratedRegex("\\btype\\s*=\\s*[\"']([^\"']*)", Ic)] private static partial Regex TypeAttr();
    [GeneratedRegex(@"javascript|ecmascript|module", Ic)] private static partial Regex JsType();
    [GeneratedRegex("^\\s*@?\"([^\"]*)\"\\s*$", Cv)] private static partial Regex LitRe();
    [GeneratedRegex(@"[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*", Cv)] private static partial Regex TypeName();
    [GeneratedRegex(@"\s*\.\s*", Cv)] private static partial Regex Dot();
    [GeneratedRegex("\\basp-action\\s*=\\s*[\"']([^\"'@]+)[\"']", Cv)] private static partial Regex AspAction();
    [GeneratedRegex("\\basp-controller\\s*=\\s*[\"']([^\"'@]+)[\"']", Cv)] private static partial Regex AspController();
    [GeneratedRegex("\\basp-(?:action|controller)\\s*=\\s*[\"'][^\"']*@", Cv)] private static partial Regex AspDynamic();

    private static string Blank(string s) => NotNewline().Replace(s, " ");

    /// <summary>Index of the ')' closing the '(' at openPos (quote aware); length-1 if unbalanced.</summary>
    private static int Balanced(string text, int openPos)
    {
        int depth = 0, i = openPos, n = text.Length;
        while (i < n)
        {
            char ch = text[i];
            if (ch == '"')
            {
                i++;
                while (i < n && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
            }
            else if (ch == '(') depth++;
            else if (ch == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
            i++;
        }
        return n - 1;
    }

    /// <summary>Top-level comma separated arguments of the call whose '(' is at openPos.</summary>
    private static List<string> SplitArgs(string text, int openPos)
    {
        int close = Balanced(text, openPos);
        string inner = close > openPos ? text[(openPos + 1)..close] : "";
        var args = new List<string>();
        var cur = new StringBuilder();
        int depth = 0, i = 0;
        while (i < inner.Length)
        {
            char ch = inner[i];
            if (ch == '"')
            {
                int j = i + 1;
                while (j < inner.Length && inner[j] != '"') j += inner[j] == '\\' ? 2 : 1;
                int e = Math.Min(j + 1, inner.Length);
                cur.Append(inner, i, e - i);
                i = j + 1;
                continue;
            }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            if (ch == ',' && depth == 0)
            {
                args.Add(cur.ToString());
                cur.Clear();
            }
            else cur.Append(ch);
            i++;
        }
        if (cur.ToString().Trim().Length > 0 || args.Count > 0) args.Add(cur.ToString());
        return args;
    }

    private static string? Lit(string? arg)
    {
        if (arg == null) return null;
        var m = LitRe().Match(arg);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Arg(List<string> args, int i) => i < args.Count ? args[i] : null;

    /// <summary>Blank `@Foo.Bar(...)`, `@(...)` and `@if`-style keywords so the JS parser does not see them as calls.</summary>
    private static string BlankRazorExprs(string s)
    {
        var o = new StringBuilder(s.Length);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '@' && i + 1 < n && s[i + 1] == '@')
            {
                o.Append("  ");
                i += 2;
                continue;
            }
            if (c == '@' && i + 1 < n && (char.IsLetter(s[i + 1]) || s[i + 1] == '_' || s[i + 1] == '('))
            {
                int j = i + 1;
                if (s[j] == '(')
                    j = Balanced(s, j) + 1;
                else
                {
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '.')) j++;
                    string word = s[(i + 1)..j];
                    if (!RazorKeywords.Contains(word.TrimEnd('.')))
                    {
                        while (j < n && s[j] == '(')
                        {
                            j = Balanced(s, j) + 1;
                            while (j < n && s[j] == '.' && j + 1 < n && char.IsLetter(s[j + 1]))
                            {
                                j++;
                                while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                            }
                        }
                    }
                }
                j = Math.Min(j, n);
                o.Append(Blank(s[i..j]));
                i = j;
                continue;
            }
            o.Append(c);
            i++;
        }
        return o.ToString();
    }

    /// <summary>'IEnumerable&lt;My.Ns.Machine&gt;' -> ['IEnumerable', 'Machine'] (outer type plus generic arguments).</summary>
    private static List<string> TypeNames(string expr)
    {
        var names = new List<string>();
        foreach (Match m in TypeName().Matches(expr))
        {
            string last = Dot().Split(m.Value)[^1];
            if (!CsPrimitives.Contains(last) && !names.Contains(last)) names.Add(last);
        }
        return names;
    }

    public static FileFacts Parse(string path, string text)
    {
        string name = path.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;
        string norm = path.Replace('\\', '/');
        var vm = ViewPath().Match(norm);
        string? controller = vm.Success ? vm.Groups[1].Value : null;
        string? defaultCtrl = controller != null && !controller.Equals("shared", StringComparison.OrdinalIgnoreCase) ? controller : null;
        int loc = SourceText.CountLines(text);

        // Razor comments @* ... *@ must not produce references; blank them (offsets preserved)
        string clean = RazorComment().Replace(text, m => Blank(m.Value));
        var li = new LineIndex(clean);

        // ---- embedded JavaScript, same offsets / line numbers as the view
        var chars = Blank(clean).ToCharArray();
        foreach (Match m in ScriptRe().Matches(clean))
        {
            var tm = TypeAttr().Match(m.Groups[1].Value);
            if (tm.Success && !JsType().IsMatch(tm.Groups[1].Value)) continue;
            var g = m.Groups[2];
            BlankRazorExprs(clean.Substring(g.Index, g.Length)).CopyTo(0, chars, g.Index, g.Length);
        }
        string scriptText = new(chars);
        var js = scriptText.Trim().Length > 0 ? ScriptParser(path, scriptText, "javascript") : new FileFacts();

        var facts = new FileFacts { Lang = "razor", Loc = loc };
        var view = new Symbol { Name = stem, Kind = "view", Line = 1, Parent = null, Visibility = "public", Lines = loc };
        if (controller != null) view.X["controller"] = controller;
        facts.Symbols.Add(view);
        foreach (var s in js.Symbols)
        {
            s.Parent ??= stem;
            facts.Symbols.Add(s);
        }
        foreach (var r in js.Refs)
        {
            if (!string.IsNullOrEmpty(r.From) && string.IsNullOrEmpty(r.FromParent)) r.FromParent = stem;
            facts.Refs.Add(r);
        }

        // ---- Razor specific references
        var local = new Dictionary<(string, string), Ref>();
        var localOrder = new List<Ref>();

        void Add(int pos, string kind, string nm, bool conventional = false, bool navigation = false)
        {
            var key = (kind, nm);
            if (local.TryGetValue(key, out var seen))
            {
                if (!navigation) seen.Extra?.Remove("navigation"); // also called, not only linked to
                return;
            }
            var r = new Ref { From = null, FromParent = stem, Kind = kind, Name = nm, Line = li.Line(pos) };
            if (conventional) r.X["conventional"] = true;
            if (navigation) r.X["navigation"] = true;
            local[key] = r;
            localOrder.Add(r);
        }

        // a link the user follows (<a href>, ActionLink, location = ...) only navigates to the page; ajax / fetch
        // url:, forms and child actions run the action for this view
        void Endpoint(int pos, string? action, string? ctrl, bool navigation = false)
        {
            ctrl = string.IsNullOrEmpty(ctrl) ? defaultCtrl : ctrl;
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(ctrl)) return;
            // normalised like every other endpoint (the Python kept "/Ctrl/Action", which never matched a route)
            Add(pos, "endpoint", Routes.Normalize($"/{ctrl}/{action}"), conventional: true, navigation: navigation);
        }

        foreach (Match m in DirectiveRe().Matches(clean))
        {
            string expr = m.Groups[2].Value.Split("@*")[0].Trim().TrimEnd(';');
            foreach (var nm in TypeNames(expr)) Add(m.Index, "cs_type", nm);
        }
        foreach (Match m in UrlAction().Matches(clean))
        {
            var args = SplitArgs(clean, m.Index + m.Length - 1);
            bool nav = NavigationBefore().IsMatch(clean[Math.Max(0, m.Index - 60)..m.Index]);
            Endpoint(m.Index, Lit(Arg(args, 0)), Lit(Arg(args, 1)), nav);
        }
        foreach (Match m in ActionLink().Matches(clean))
        {
            var args = SplitArgs(clean, m.Index + m.Length - 1);
            Endpoint(m.Index, Lit(Arg(args, 1)), Lit(Arg(args, 2)), navigation: true);
        }
        foreach (var rx in new[] { BeginForm(), RenderAction() })
            foreach (Match m in rx.Matches(clean))
            {
                var args = SplitArgs(clean, m.Index + m.Length - 1);
                Endpoint(m.Index, Lit(Arg(args, 0)), Lit(Arg(args, 1)));
            }
        foreach (Match m in TagHelper().Matches(clean))
        {
            string tag = m.Value;
            var am = AspAction().Match(tag);
            var cm = AspController().Match(tag);
            if (AspDynamic().IsMatch(tag)) continue;
            bool link = AnchorTag().IsMatch(tag); // <a asp-action> navigates; <form asp-action> posts
            if (cm.Success && !am.Success) Endpoint(m.Index, "Index", cm.Groups[1].Value, link);
            else if (am.Success) Endpoint(m.Index, am.Groups[1].Value, cm.Success ? cm.Groups[1].Value : null, link);
        }
        foreach (var rx in new[] { PartialCall(), PartialTag() })
            foreach (Match m in rx.Matches(clean))
                Add(m.Index, "partial", m.Groups[1].Value);

        facts.Refs.AddRange(localOrder);
        return facts;
    }
}
