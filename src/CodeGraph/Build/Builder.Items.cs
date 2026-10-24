using System.Text.Json.Nodes;

namespace CodeGraph.Build;

// Items from extractors (your own XML/JSON formats, docs/design/extractors.md): nodes, their links, the DB objects
// their embedded SQL defines, and code strings that name them.
public sealed partial class Builder
{
    /// <summary>Lower-case item name -> item node ids (any kind).</summary>
    private readonly Dictionary<string, List<string>> _itemsByName = [];

    private void Items()
    {
        var withItems = _records.Where(r => _g.HasNode(r.Id) && r.Facts.Extra?["items"] is JsonArray { Count: > 0 }).ToList();
        if (withItems.Count == 0) return;
        // 1. the nodes, so links between items resolve whatever the file order
        foreach (var r in withItems)
            foreach (var it in Item(r))
            {
                string id = XStr(it, "id")!, kind = XStr(it, "kind")!, name = XStr(it, "name")!;
                if (_g.GetNode(id) is { } existing)
                {
                    if (existing.Str("file") != r.Id)
                    {
                        var also = existing["also_defined_in"] as JsonArray ?? [];
                        also.Add((JsonNode?)r.Id);
                        existing["also_defined_in"] = also;
                    }
                }
                else
                {
                    _g.Node(id, ("kind", kind), ("name", name), ("extractor", XStr(r.Facts.Extra, "extractor")),
                        ("file", r.Id), ("repo", r.Repo), ("line", it["line"]?.DeepClone()), ("attrs", it["attrs"]),
                        ("files", it["files"] is JsonArray { Count: > 0 } fs ? fs : null));
                    GetOrAdd(_itemsByName, name.ToLowerInvariant()).Add(id);
                    Inc(_stats, $"items_{kind}");
                }
                _g.AddEdge(id, r.Id, "defined_in");
            }
        // 2. their links and the DB objects their SQL defines
        foreach (var r in withItems)
        {
            foreach (var it in Item(r))
            {
                string id = XStr(it, "id")!;
                foreach (var l in (it["links"] as JsonArray ?? []).OfType<JsonObject>())
                    ItemLink(r, id, l);
            }
            foreach (var s in r.Facts.Symbols)
                if (XStr(s.Extra, "item") is { } item && XStr(s.Extra, "key") is { } key && _db.TryGetValue(key, out var dbId))
                    ItemEdge(item, dbId, "defines", s.Line);
        }
    }

    private static IEnumerable<JsonObject> Item(Rec r) => (r.Facts.Extra?["items"] as JsonArray ?? []).OfType<JsonObject>();

    private void ItemEdge(string from, string to, string relation, int? line, string? via = null) =>
        _g.AddEdge(from, to, relation, attrs: [("line", line), ("via", via), ("custom", true), ("label", relation)]);

    private void ItemLink(Rec r, string itemId, JsonObject l)
    {
        string relation = XStr(l, "relation") ?? "links_to", value = XStr(l, "value") ?? "", to = XStr(l, "to") ?? "auto";
        string? kind = XStr(l, "kind");
        int? line = l["line"] is JsonValue lv && lv.TryGetValue(out int li) ? li : null;
        string? target = to switch
        {
            "db" => DbLookup(value),
            "project" => ProjectByName(value),
            "file" => FileByName(r.Id, value),
            "symbol" => SymbolByName(r.Id, value),
            "release" => Release(value, XStr(r.Facts.Extra, "extractor")),
            "new" => null,
            _ => DbLookup(value) ?? ProjectByName(value) ?? FileByName(r.Id, value) ?? SymbolByName(r.Id, value) ?? ItemByName(value),
        };
        if (target is null && !string.IsNullOrEmpty(kind) && to is "auto" or "new")
        {
            target = Parsers.ExtractorParser.ItemId(kind, value);
            if (!_g.HasNode(target))
            {
                _g.Node(target, ("kind", kind), ("name", value), ("extractor", XStr(r.Facts.Extra, "extractor")));
                GetOrAdd(_itemsByName, value.ToLowerInvariant()).Add(target);
                Inc(_stats, $"items_{kind}");
            }
        }
        if (target is null && (relation == "uses_file" || to == "file"))
        {
            // a file that isn't scanned (binary report, image ...): still a node, so its users meet there
            string name = Paths.Base(value);
            target = $"fileref:{name.ToLowerInvariant()}";
            if (!_g.HasNode(target)) _g.Node(target, ("kind", "file_ref"), ("name", name));
        }
        if (target is null)
        {
            Inc(_unresolved, $"extract:{XStr(r.Facts.Extra, "extractor")}: {relation} {value}");
            Inc(_stats, "extract_links_unresolved");
            return;
        }
        ItemEdge(itemId, target, relation, line);
    }

    /// <summary>The release node for a version ("24.00.00" -> rel:24.0.0), created if no release file or branch has it yet.</summary>
    private string Release(string version, string? extractor)
    {
        string v = string.Join('.', version.Split('.').Select(p => int.TryParse(p, out int i) ? i.ToString() : p));
        string id = $"rel:{v}";
        if (!_g.HasNode(id))
        {
            _g.Node(id, ("kind", "release"), ("name", v), ("status", $"from {extractor}"));
            Inc(_stats, "releases_from_extractors");
        }
        return id;
    }

    private string? ProjectByName(string value)
    {
        var ids = _projByOutput.GetValueOrDefault(value.ToLowerInvariant()) ?? _projByOutput.GetValueOrDefault(Paths.Stem(value));
        return ids is { Count: 1 } ? ids.First() : null;
    }

    private string? FileByName(string fromFile, string value)
    {
        var cands = (_byBasename.GetValueOrDefault(Paths.Base(value).ToLowerInvariant()) ?? []).Select(c => c.Id);
        var picked = Pick(fromFile, cands);
        return picked.Count == 1 ? picked[0] : null;
    }

    private string? SymbolByName(string fromFile, string value)
    {
        int dot = value.LastIndexOf('.');
        var cands = new List<string>();
        if (dot > 0)
        {
            // Type.Member
            string type = LastPart(value[..dot]).ToLowerInvariant(), member = value[(dot + 1)..].ToLowerInvariant();
            foreach (var ((_, t), byName) in _members)
                if (t == type && byName.TryGetValue(member, out var ids)) cands.AddRange(ids);
        }
        foreach (var ((_, n), ids) in _typeIndex)
            if (n == value.ToLowerInvariant()) cands.AddRange(ids);
        var picked = Pick(fromFile, cands);
        return picked.Count == 1 ? picked[0] : null;
    }

    private string? ItemByName(string value) =>
        _itemsByName.GetValueOrDefault(value.ToLowerInvariant()) is { Count: 1 } ids ? ids[0] : null;

    /// <summary>Code string literals equal to an item's name or report file (extractor option referenced_by) -> "opens".</summary>
    private void ItemReferences()
    {
        var targets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _records)
        {
            if (r.Facts.Extra?["referenced_by"] is not JsonArray rb) continue;
            var by = StrList(rb).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var it in Item(r))
            {
                string id = XStr(it, "id")!;
                if (by.Contains("name")) GetOrAdd(targets, XStr(it, "name")!).Add(id);
                if (by.Contains("uses_file"))
                    foreach (var f in StrList(it["files"]))
                    {
                        GetOrAdd(targets, f).Add(id);
                        GetOrAdd(targets, Paths.Base(f)).Add(id);
                    }
            }
        }
        if (targets.Count == 0) return;
        foreach (var r in _records)
        {
            if (r.Facts.Extra?["strings"] is not JsonArray strings || !_g.HasNode(r.Id)) continue;
            List<(string Sid, int Start, int End)>? routines = null;
            foreach (var pair in strings.OfType<JsonArray>())
            {
                if (pair.Count < 2 || pair[1]?.ToString() is not { } text) continue;
                if (!targets.TryGetValue(text, out var hits) && !targets.TryGetValue(Paths.Base(text), out hits)) continue;
                int line = pair[0] is JsonValue jv && jv.TryGetValue(out int ln) ? ln : 0;
                routines ??= RoutineSpans(r);
                string caller = routines.Where(x => x.Start <= line && line <= x.End)
                    .OrderBy(x => x.End - x.Start).Select(x => x.Sid).FirstOrDefault() ?? r.Id;
                foreach (var item in hits)
                    ItemEdge(caller, item, "opens", line == 0 ? null : line, "string");
            }
        }
    }

    private List<(string Sid, int Start, int End)> RoutineSpans(Rec r)
    {
        var spans = new List<(string, int, int)>();
        foreach (var s in r.Facts.Symbols)
        {
            if (!RoutineKinds.Contains(s.Kind) || s.Lines <= 0) continue;
            string qual = !string.IsNullOrEmpty(s.Parent) ? $"{s.Parent}.{s.Name}" : s.Name;
            string sid = $"sym:{r.Repo}:{r.Rel}#{qual}";
            if (_g.HasNode(sid)) spans.Add((sid, s.Line, s.Line + s.Lines - 1));
        }
        return spans;
    }
}
