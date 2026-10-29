using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>
/// Client-side routes (Vue Router style object tables) and navigation to them. A route is an object literal with
/// `path:` and at least one of `component`, `components`, `children`, `redirect`, `name` - the router's own shape,
/// not a naming convention. Navigation: &lt;router-link to&gt;, :to, and push / replace / navigateTo in code that uses
/// a router.
/// </summary>
internal static partial class JsRoutes
{
    private const RegexOptions O = RegexOptions.CultureInvariant;

    [GeneratedRegex(@"(?<![\w$.])path\s*:\s*", O)] private static partial Regex PathKey();
    [GeneratedRegex(@"(?<![\w$.])(name|component|components|redirect|meta|children)\s*:\s*", O)] private static partial Regex RouteKey();
    [GeneratedRegex(@"\G\(\s*\)\s*=>\s*import\s*\(\s*", O)] private static partial Regex LazyImport();
    [GeneratedRegex(@"\G([A-Za-z_$][\w$]*)\b(?!\s*\()", O)] private static partial Regex Identifier();
    [GeneratedRegex(@"(?<![\w$.])(\w+)\s*:\s*", O)] private static partial Regex AnyKey();
    [GeneratedRegex(@"\G(true|false|-?\d+(?:\.\d+)?)\b", O)] private static partial Regex Scalar();
    [GeneratedRegex(@"\bvue-router\b|\buseRouter\s*\(|\$router\b|\buseNavigate\s*\(|\bnavigateTo\s*\(", O)] private static partial Regex UsesRouter();
    [GeneratedRegex(@"\.(?:push|replace)\s*\(\s*|\bnavigateTo\s*\(\s*", O)] private static partial Regex NavigateCall();
    [GeneratedRegex(@"<(?:router-link|RouterLink|nuxt-link|NuxtLink)\b([^>]*)>", O | RegexOptions.IgnoreCase)] private static partial Regex LinkTag();
    [GeneratedRegex("""(?<![\w:-])(:|v-bind:)?to\s*=\s*(?:"([^"]*)"|'([^']*)')""", O)] private static partial Regex ToAttr();
    [GeneratedRegex("""(?<![\w-])(?:@|v-on:)[\w.:-]+\s*=\s*(?:"([^"]*)"|'([^']*)')""", O)] private static partial Regex HandlerAttr();

    /// <summary>A string literal starting at <paramref name="at"/> in the original text: its value, or null.</summary>
    private static string? Literal(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        if (at >= text.Length || text[at] is not ('\'' or '"' or '`')) return null;
        char q = text[at];
        int end = text.IndexOf(q, at + 1);
        return end < 0 ? null : text[(at + 1)..end];
    }

    /// <summary>Nesting of position <paramref name="to"/> relative to <paramref name="from"/> ({[( count).</summary>
    private static int Level(string code, int from, int to)
    {
        int d = 0;
        for (int i = from; i < to && i < code.Length; i++)
            if (code[i] is '{' or '[' or '(') d++;
            else if (code[i] is '}' or ']' or ')') d--;
        return d;
    }

    [GeneratedRegex(@"(?<![\w$.])(?:routes|children)\s*:\s*$", O)] private static partial Regex RoutesKeyBefore();
    [GeneratedRegex(@"(?<![\w$.])(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::\s*([^=]+?))?\s*=\s*$", O)] private static partial Regex VarBefore();

    /// <summary>Innermost unclosed bracket ({ [ () before <paramref name="pos"/>, or -1.</summary>
    private static int OpenerBefore(string code, int pos)
    {
        int depth = 0;
        for (int i = pos - 1; i >= 0; i--)
            if (code[i] is '}' or ']' or ')') depth++;
            else if (code[i] is '{' or '[' or '(') { if (depth == 0) return i; depth--; }
        return -1;
    }

    /// <summary>
    /// Whether the object at <paramref name="objStart"/> is an entry of a route list: an array given to the router
    /// as `routes:` / `children:`, or a variable passed as `routes` or typed as route records - the router's API, so
    /// a breadcrumb list of { name, path } objects is not taken for routes.
    /// </summary>
    private static bool InRouteList(string code, int objStart)
    {
        int arr = OpenerBefore(code, objStart);
        if (arr < 0 || code[arr] != '[') return false;
        string before = code[Math.Max(0, arr - 200)..arr];
        if (RoutesKeyBefore().IsMatch(before)) return true;
        var v = VarBefore().Match(before);
        if (!v.Success) return false;
        string name = v.Groups[1].Value;
        return (v.Groups[2].Success && v.Groups[2].Value.Contains("Route", StringComparison.Ordinal))
               || Regex.IsMatch(code, $@"(?<![\w$.])routes\s*:\s*{Regex.Escape(name)}\b")
               || (name == "routes" && Regex.IsMatch(code, @"[{,]\s*routes\s*[,}]"));
    }

    private static int OpenBefore(string code, int pos)
    {
        int depth = 0;
        for (int i = pos - 1; i >= 0; i--)
            if (code[i] == '}') depth++;
            else if (code[i] == '{') { if (depth == 0) return i; depth--; }
        return -1;
    }

    /// <summary>`{ name: 'x' }` / `{ path: '/x' }` at <paramref name="at"/>: the (path, name) it names.</summary>
    private static (string? Path, string? Name) TargetObject(string code, string text, int at)
    {
        if (at >= code.Length || code[at] != '{') return (null, null);
        int end = Scrub.MatchBrace(code, at);
        string? path = null, name = null;
        foreach (Match k in AnyKey().Matches(code[..Math.Min(end, code.Length)], at + 1))
        {
            if (Level(code, at + 1, k.Index) != 0) continue;
            if (k.Groups[1].Value == "path") path ??= Literal(text, k.Index + k.Length);
            else if (k.Groups[1].Value == "name") name ??= Literal(text, k.Index + k.Length);
        }
        return (path, name);
    }

    private static string Join(string parent, string child) =>
        child.StartsWith('/') ? child : parent.TrimEnd('/') + "/" + child;

    /// <summary>The route table of a script, or null when it has none.</summary>
    public static JsonArray? Table(string code, string text, LineIndex li)
    {
        var routes = new List<(int Start, int End, JsonObject Obj, string Raw)>();
        foreach (Match m in PathKey().Matches(code))
        {
            string? raw = Literal(text, m.Index + m.Length);
            if (raw is null) continue;
            int start = OpenBefore(code, m.Index);
            if (start < 0 || Level(code, start + 1, m.Index) != 0 || !InRouteList(code, start)) continue;
            int end = Scrub.MatchBrace(code, start);
            var o = new JsonObject();
            bool isRoute = false;
            foreach (Match k in RouteKey().Matches(code[..Math.Min(end, code.Length)], start + 1))
            {
                if (Level(code, start + 1, k.Index) != 0) continue;
                int v = k.Index + k.Length;
                isRoute = true;
                switch (k.Groups[1].Value)
                {
                    case "name" when Literal(text, v) is { } n:
                        o["name"] = n;
                        break;
                    case "component" or "components":
                        if (LazyImport().Match(code, v) is { Success: true } lz && Literal(text, lz.Index + lz.Length) is { } spec)
                            o["lazy"] = spec;
                        else if (Identifier().Match(code, v) is { Success: true } id)
                            o["component"] = id.Groups[1].Value;
                        break;
                    case "redirect":
                        if (Literal(text, v) is { } rp) o["redirect"] = rp;
                        else if (TargetObject(code, text, v) is var (tp, tn) && (tp ?? tn) is not null)
                        {
                            if (tp is not null) o["redirect"] = tp;
                            if (tn is not null) o["redirect_name"] = tn;
                        }
                        break;
                    case "meta" when v < code.Length && code[v] == '{':
                    {
                        int mEnd = Scrub.MatchBrace(code, v);
                        var meta = new JsonObject();
                        foreach (Match mk in AnyKey().Matches(code[..Math.Min(mEnd, code.Length)], v + 1))
                        {
                            if (Level(code, v + 1, mk.Index) != 0) continue;
                            int at = mk.Index + mk.Length;
                            if (Literal(text, at) is { } s) meta[mk.Groups[1].Value] = s;
                            else if (Scalar().Match(code, at) is { Success: true } sc) meta[mk.Groups[1].Value] = sc.Value;
                        }
                        if (meta.Count > 0) o["meta"] = meta;
                        break;
                    }
                }
            }
            if (!isRoute) continue;
            o["line"] = li.Line(m.Index);
            routes.Add((start, end, o, raw));
        }
        if (routes.Count == 0) return null;
        // full paths: a child route's path is relative to its parent's
        var result = new JsonArray();
        foreach (var r in routes)
        {
            var chain = routes.Where(p => p.Start < r.Start && r.End <= p.End).OrderBy(p => p.Start).ToList();
            string full = "";
            foreach (var p in chain) full = Join(full, p.Raw);
            string? parent = chain.Count > 0 ? (full.Length == 0 ? "/" : full) : null;
            full = Join(full, r.Raw);
            if (full.Length == 0) full = "/";
            r.Obj["path"] = full;
            if (parent is not null) r.Obj["parent"] = parent;
            result.Add((JsonNode)r.Obj);
        }
        return result;
    }

    /// <summary>push / replace / navigateTo targets in code that uses a router: (offset, path, name).</summary>
    public static IEnumerable<(int Offset, string? Path, string? Name)> ScriptLinks(string code, string text)
    {
        if (!UsesRouter().IsMatch(text)) return [];
        return Navigations(code, text);
    }

    private static IEnumerable<(int Offset, string? Path, string? Name)> Navigations(string code, string text)
    {
        foreach (Match m in NavigateCall().Matches(code))
        {
            int at = m.Index + m.Length;
            if (Literal(text, at) is { } p) yield return (m.Index, p, null);
            else if (TargetObject(code, text, at) is var (tp, tn) && (tp ?? tn) is not null) yield return (m.Index, tp, tn);
        }
    }

    /// <summary>
    /// &lt;router-link to="/x"&gt; / :to="'/x'" / :to="{ name: 'x' }" in a template, and router.push(...) written in an
    /// event handler (@click="router.push({ name: 'x' })") when the file uses a router: (offset, path, name).
    /// </summary>
    public static IEnumerable<(int Offset, string? Path, string? Name)> TemplateLinks(string template, string fileText)
    {
        if (UsesRouter().IsMatch(fileText))
            foreach (Match h in HandlerAttr().Matches(template))
            {
                var g = h.Groups[1].Success ? h.Groups[1] : h.Groups[2];
                foreach (var (at, p, n) in Navigations(Scrub.CLike(g.Value, verbatimAt: false, backtick: true).Code, g.Value))
                    yield return (g.Index + at, p, n);
            }
        foreach (Match tag in LinkTag().Matches(template))
        {
            var to = ToAttr().Match(tag.Groups[1].Value);
            if (!to.Success) continue;
            string value = to.Groups[2].Success ? to.Groups[2].Value : to.Groups[3].Value;
            if (!to.Groups[1].Success) { yield return (tag.Index, value, null); continue; }
            string expr = value.Trim();
            if (Literal(expr, 0) is { } p) yield return (tag.Index, p, null);
            else if (expr.StartsWith('{') && TargetObject(expr, expr, 0) is var (tp, tn) && (tp ?? tn) is not null)
                yield return (tag.Index, tp, tn);
        }
    }

    /// <summary>A navigation ref (a candidate: it only counts when it matches a route).</summary>
    public static Model.Ref LinkRef(string? from, string? fromParent, int line, string? path, string? name)
    {
        var r = new Model.Ref { From = from, FromParent = fromParent, Kind = "route_link", Name = path is null ? "" : Routes.Normalize(path), Line = line };
        if (name is not null) r.X["route_name"] = name;
        r.X["candidate"] = true;
        return r;
    }
}
