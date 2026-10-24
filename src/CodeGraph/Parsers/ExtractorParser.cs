using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>
/// Reads one of your own XML/JSON formats with the rules of an <see cref="ExtractorConfig"/>.
/// Output: facts.X["items"] = [{ id, kind, name, line, attrs, links: [{relation, value, to, kind, line}], files }],
/// plus DB objects and refs from embedded SQL (marked with X["item"] = the item's node id).
/// </summary>
public static class ExtractorParser
{
    private static readonly JsonDocumentOptions JsonOpts = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Node id of an extractor item: one node per kind + name, across all files.</summary>
    public static string ItemId(string kind, string name) => $"item:{kind.ToLowerInvariant()}:{name.Trim().ToLowerInvariant()}";

    public static FileFacts Parse(string path, string text, ExtractorConfig x)
    {
        bool json = x.Format?.ToLowerInvariant() == "json" ||
                    x.Format is null && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        var facts = new FileFacts { Lang = json ? "json" : "xml", Loc = SourceText.CountLines(text) };
        IEnumerable<IReader> items = json ? JsonReader.Items(text, x) : XmlReader.Items(text, x);
        var list = new JsonArray();
        foreach (var item in items)
        {
            var nameVal = item.Values(x.Node.Name).FirstOrDefault(v => v.Value.Trim().Length > 0);
            if (nameVal.Value is null)
                throw new FormatException($"extractor '{x.Name}': no value for node name '{x.Node.Name}'");
            string name = nameVal.Value.Trim();
            string id = ItemId(x.Node.Kind, name);
            var attrs = new JsonObject();
            foreach (var (label, p) in x.Node.Attrs)
                if (item.Values(p).Select(v => v.Value.Trim()).FirstOrDefault(v => v.Length > 0) is { } av)
                    attrs[label] = av;
            var links = new JsonArray();
            var files = new JsonArray();
            foreach (var l in x.Links)
            {
                if (!string.IsNullOrEmpty(l.Sql))
                {
                    foreach (var (sql, line) in item.Values(l.Sql))
                        AddSql(facts, path, sql, line, id);
                    continue;
                }
                var rewrite = string.IsNullOrEmpty(l.Pattern) ? null
                    : new System.Text.RegularExpressions.Regex(l.Pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                foreach (var (raw, line) in item.Values(l.Value!))
                {
                    string v = raw.Trim();
                    if (rewrite is not null && rewrite.IsMatch(v)) v = rewrite.Replace(v, l.Replace ?? "").Trim();
                    if (v.Length == 0) continue;
                    var lo = new JsonObject { ["relation"] = l.Relation, ["value"] = v, ["to"] = l.To, ["line"] = line };
                    if (!string.IsNullOrEmpty(l.Kind)) lo["kind"] = l.Kind;
                    links.Add((JsonNode)lo);
                    if (l.Relation == "uses_file") files.Add((JsonNode?)v);
                }
            }
            list.Add((JsonNode)new JsonObject
            {
                ["id"] = id, ["kind"] = x.Node.Kind, ["name"] = name, ["line"] = nameVal.Line,
                ["attrs"] = attrs, ["links"] = links, ["files"] = files,
            });
        }
        facts.X["extractor"] = x.Name;
        facts.X["items"] = list;
        if (x.ReferencedBy.Count > 0)
            facts.X["referenced_by"] = new JsonArray(x.ReferencedBy.Select(r => (JsonNode?)r).ToArray());
        return facts;
    }

    /// <summary>Embedded T-SQL: definitions become DB objects, statements become refs from the item.</summary>
    private static void AddSql(FileFacts facts, string path, string sql, int line, string itemId)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        var f = SqlParser.Parse(path, sql);
        int offset = Math.Max(0, line - 1);
        foreach (var s in f.Symbols)
        {
            s.Line += offset;
            s.X["item"] = itemId;
            facts.Symbols.Add(s);
        }
        foreach (var r in f.Refs)
        {
            r.Line += offset;
            r.X["item"] = itemId;
            facts.Refs.Add(r);
        }
    }

    /// <summary>One item of a document: evaluates paths relative to it.</summary>
    private interface IReader
    {
        IEnumerable<(string Value, int Line)> Values(string path);
    }

    // ------------------------------------------------------------------ XML (XPath 1.0)

    private sealed class XmlReader(XElement context, IXmlNamespaceResolver? ns) : IReader
    {
        public static IEnumerable<IReader> Items(string text, ExtractorConfig x)
        {
            XDocument doc;
            try { doc = XDocument.Parse(text, LoadOptions.SetLineInfo); }
            catch (XmlException ex) { throw new FormatException($"extractor '{x.Name}': not valid XML ({ex.Message})"); }
            if (doc.Root is null) yield break;
            XmlNamespaceManager? ns = null;
            if (x.Namespaces.Count > 0)
            {
                ns = new XmlNamespaceManager(new NameTable());
                foreach (var (prefix, uri) in x.Namespaces) ns.AddNamespace(prefix, uri);
            }
            else
                StripNamespaces(doc.Root);
            if (string.IsNullOrEmpty(x.Items))
            {
                yield return new XmlReader(doc.Root, ns);
                yield break;
            }
            foreach (var o in Eval(doc.Root, x.Items, ns))
                if (o is XElement e) yield return new XmlReader(e, ns);
        }

        public IEnumerable<(string Value, int Line)> Values(string path)
        {
            foreach (var o in Eval(context, path, ns))
                switch (o)
                {
                    case XElement e: yield return (e.Value, LineOf(e)); break;
                    case XAttribute a: yield return (a.Value, LineOf(a)); break;
                    case XText t: yield return (t.Value, LineOf(t)); break;
                    case string s: yield return (s, LineOf(context)); break;
                    case double or bool: yield return (Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)!, LineOf(context)); break;
                }
        }

        private static IEnumerable<object> Eval(XElement context, string path, IXmlNamespaceResolver? ns)
        {
            object result;
            try { result = context.XPathEvaluate(path, ns); }
            catch (XPathException ex) { throw new FormatException($"bad XPath '{path}': {ex.Message}"); }
            return result is System.Collections.IEnumerable list and not string ? list.Cast<object>() : [result];
        }

        private static int LineOf(XObject o) => o is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 1;

        private static void StripNamespaces(XElement root)
        {
            foreach (var e in root.DescendantsAndSelf())
            {
                e.Name = e.Name.LocalName;
                var nsAttrs = e.Attributes().Where(a => a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None).ToList();
                foreach (var a in nsAttrs)
                {
                    a.Remove();
                    if (!a.IsNamespaceDeclaration) e.SetAttributeValue(a.Name.LocalName, a.Value);
                }
            }
        }
    }

    // ------------------------------------------------------------------ JSON (a.b, a[*].c, a.*, a[0])

    private sealed class JsonReader(JsonNode? context, string text) : IReader
    {
        public static IEnumerable<IReader> Items(string text, ExtractorConfig x)
        {
            JsonNode? root;
            try { root = JsonNode.Parse(text, documentOptions: JsonOpts); }
            catch (JsonException ex) { throw new FormatException($"extractor '{x.Name}': not valid JSON ({ex.Message})"); }
            if (string.IsNullOrEmpty(x.Items)) return [new JsonReader(root, text)];
            return Select(root, x.Items).Select(n => (IReader)new JsonReader(n, text)).ToList();
        }

        public IEnumerable<(string Value, int Line)> Values(string path)
        {
            foreach (var n in Select(context, path))
            {
                if (n is JsonArray or JsonObject) continue;
                string v = n is JsonValue jv && jv.TryGetValue(out string? s) ? s : n?.ToJsonString() ?? "";
                yield return (v, LineOf(v));
            }
        }

        // JSON nodes have no positions: use the line of the first occurrence of the value in the text
        private int LineOf(string value)
        {
            int i = value.Length > 0 ? text.IndexOf(JsonSerializer.Serialize(value, CodeGraphJson.Default.String), StringComparison.Ordinal) : -1;
            return i < 0 ? 1 : new LineIndex(text).Line(i);
        }

        private static IEnumerable<JsonNode?> Select(JsonNode? start, string path)
        {
            IEnumerable<JsonNode?> cur = [start];
            string p = path.Trim();
            if (p.StartsWith('$')) p = p[1..];
            foreach (var raw in p.Replace("[", ".[").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                string step = raw.Trim();
                cur = cur.SelectMany(n => Step(n, step)).ToList();
            }
            return cur;
        }

        private static IEnumerable<JsonNode?> Step(JsonNode? n, string step)
        {
            if (step is "*" or "[*]")
            {
                if (n is JsonArray a) return a;
                if (n is JsonObject o) return o.Select(kv => kv.Value);
                return [];
            }
            if (step.StartsWith('[') && step.EndsWith(']') && int.TryParse(step[1..^1], out int idx))
                return n is JsonArray a && idx >= 0 && idx < a.Count ? [a[idx]] : [];
            return n is JsonObject obj && obj.TryGetPropertyValue(step, out var v) ? [v] : [];
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class CodeGraphJson : System.Text.Json.Serialization.JsonSerializerContext;
