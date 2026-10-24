using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeGraph.Model;
using CodeGraph.Text;

namespace CodeGraph.Parsers;

/// <summary>WiX installer sources (.wxs .wxi). Project files (.wixproj) are handled by ProjectParser.
/// Port of codegraph/parsers/wix.py.</summary>
public static partial class WixParser
{
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions Cv = RegexOptions.CultureInvariant;

    [GeneratedRegex(@"<([A-Za-z][\w:]*)\b([^<>]*?)/?>", Cv | RegexOptions.Singleline)] private static partial Regex TagRe();
    [GeneratedRegex("([\\w:]+)\\s*=\\s*\"([^\"]*)\"", Cv | RegexOptions.Singleline)] private static partial Regex AttrRe();
    [GeneratedRegex(@"\$\(var\.([\w.\-]+?)\.(?:TargetPath|TargetDir|TargetFileName|TargetName|ProjectDir|TargetBaseName|TargetExt)\)", Ic)]
    private static partial Regex VarProj();
    [GeneratedRegex(@"<!--.*?-->", Cv | RegexOptions.Singleline)] private static partial Regex Comment();
    [GeneratedRegex(@"[^\n]", Cv)] private static partial Regex NotNewline();
    [GeneratedRegex(@"\$\([^)]*\)", Cv)] private static partial Regex AnyVar();
    [GeneratedRegex(@"\s*\$\([^)]*\)", Cv)] private static partial Regex SpaceVar();
    [GeneratedRegex(@"[\s\-:,]+$", Cv)] private static partial Regex TrailJunk();
    [GeneratedRegex(@"[\\/]", Cv)] private static partial Regex Sep();

    private static readonly string[] SourceAttrs = ["Source", "SourceFile", "src"];

    private static readonly Dictionary<string, string> ShipTags = new()
    {
        ["file"] = "file", ["merge"] = "merge module", ["msipackage"] = "msi", ["exepackage"] = "exe",
        ["msupackage"] = "msu", ["payload"] = "payload", ["binary"] = "binary", ["bundlepackage"] = "bundle",
        ["msppackage"] = "msp",
    };

    private static readonly Dictionary<string, string> RefTags = new()
    {
        ["featureref"] = "feature", ["componentgroupref"] = "component_group", ["mergeref"] = "merge",
        ["featuregroupref"] = "feature", ["packagegroupref"] = "package_group",
    };

    private static string Basename(string src)
    {
        src = VarProj().Replace(src, "");
        src = AnyVar().Replace(src, "");
        return Sep().Split(src.Trim())[^1];
    }

    private static string? A(Dictionary<string, string> attrs, string key) =>
        attrs.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static void SetX(Symbol s, string key, string? value)
    {
        if (value != null) s.X[key] = value;
    }

    private static void Set(JsonObject o, string key, string? value)
    {
        if (value != null) o[key] = value;
    }

    public static FileFacts Parse(string path, string text)
    {
        // drop comments, keep offsets
        string code = Comment().Replace(text, m => NotNewline().Replace(m.Value, " "));
        var li = new LineIndex(code);
        var facts = new FileFacts { Lang = "wix", Loc = SourceText.CountLines(text) };
        string? installer = null;
        string? currentGroup = null;
        var services = new JsonArray();
        var customActions = new JsonArray();
        var shortcuts = new JsonArray();
        foreach (Match m in TagRe().Matches(code))
        {
            string tag = m.Groups[1].Value.Split(':')[^1];
            string low = tag.ToLowerInvariant();
            var attrs = new Dictionary<string, string>();
            foreach (Match am in AttrRe().Matches(m.Groups[2].Value)) attrs[am.Groups[1].Value] = am.Groups[2].Value;
            int line = li.Line(m.Index);
            if (low is "product" or "bundle" or "module" || (low == "package" && A(attrs, "Name") != null))
            {
                if (installer == null)
                {
                    installer = A(attrs, "Name") ?? A(attrs, "Id") ?? "installer";
                    installer = SpaceVar().Replace(installer, "");
                    installer = TrailJunk().Replace(installer, "").Trim();
                    if (installer.Length == 0) installer = A(attrs, "Id") ?? "installer";
                    var sym = new Symbol { Name = installer, Kind = "installer", Line = line, Parent = null, Visibility = "public" };
                    sym.X["installer_type"] = low;
                    SetX(sym, "version", A(attrs, "Version"));
                    SetX(sym, "manufacturer", A(attrs, "Manufacturer"));
                    SetX(sym, "upgrade_code", A(attrs, "UpgradeCode"));
                    facts.Symbols.Add(sym);
                }
            }
            else if (low is "feature" or "componentgroup" or "packagegroup" && A(attrs, "Id") != null)
            {
                string kind = low switch { "feature" => "feature", "componentgroup" => "component_group", _ => "package_group" };
                var sym = new Symbol { Name = attrs["Id"], Kind = kind, Line = line, Parent = null, Visibility = "public" };
                SetX(sym, "title", A(attrs, "Title"));
                facts.Symbols.Add(sym);
                currentGroup = attrs["Id"];
            }
            else if (RefTags.TryGetValue(low, out var refKind) && A(attrs, "Id") != null)
            {
                var r = new Ref { From = null, FromParent = currentGroup ?? installer, Kind = "wix_ref", Name = attrs["Id"], Line = line };
                r.X["ref_kind"] = refKind;
                facts.Refs.Add(r);
            }
            if (ShipTags.TryGetValue(low, out var shipKind))
            {
                string? src = SourceAttrs.Select(a => A(attrs, a)).FirstOrDefault(v => v != null) ?? A(attrs, "Name");
                if (src != null)
                {
                    var pm = VarProj().Match(src);
                    string name = Basename(src);
                    if (name.Length == 0) name = A(attrs, "Name") ?? "";
                    var r = new Ref { From = null, FromParent = currentGroup ?? installer, Kind = "ships", Name = name, Line = line };
                    if (pm.Success) r.X["project"] = pm.Groups[1].Value;
                    r.X["ship_kind"] = shipKind;
                    facts.Refs.Add(r);
                }
            }
            else if (low == "serviceinstall" && A(attrs, "Name") != null)
                services.Add((JsonNode?)JsonValue.Create(attrs["Name"]));
            else if (low == "customaction" && A(attrs, "Id") != null)
            {
                var ca = new JsonObject();
                foreach (var k in new[] { "Id", "BinaryKey", "DllEntry", "ExeCommand", "FileKey" })
                    Set(ca, k, A(attrs, k));
                customActions.Add((JsonNode?)ca);
            }
            else if (low == "shortcut" && A(attrs, "Name") != null)
                shortcuts.Add((JsonNode?)JsonValue.Create(attrs["Name"]));
        }
        // project variables referenced anywhere ($(var.Proj.TargetPath)) -> installer packages that project
        var projs = VarProj().Matches(code).Select(x => x.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal);
        foreach (var pm in projs)
        {
            var r = new Ref { From = null, FromParent = installer, Kind = "ships", Name = "", Line = 1 };
            r.X["project"] = pm;
            r.X["ship_kind"] = "project output";
            facts.Refs.Add(r);
        }
        facts.X["services"] = services;
        facts.X["custom_actions"] = new JsonArray(customActions.Take(100).Select(n => (JsonNode?)n!.DeepClone()).ToArray());
        facts.X["shortcuts"] = new JsonArray(shortcuts.Take(100).Select(n => (JsonNode?)n!.DeepClone()).ToArray());
        return facts;
    }
}
