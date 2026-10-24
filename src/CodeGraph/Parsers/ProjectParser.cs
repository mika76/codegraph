using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeGraph.Model;

namespace CodeGraph.Parsers;

/// <summary>Project and solution files: .vbp .vbg .csproj .vbproj .fsproj .vcxproj .vcproj .dsp .sqlproj .wixproj
/// .sln .slnx. Port of codegraph/parsers/projects.py.</summary>
public static partial class ProjectParser
{
    private const RegexOptions Ic = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions Cv = RegexOptions.CultureInvariant;

    private static readonly HashSet<string> SolutionProjectExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vbp", ".csproj", ".vbproj", ".vcxproj", ".vcproj", ".dsp", ".sqlproj", ".fsproj", ".wixproj",
        ".vdproj", ".shproj", ".dbproj",
    };

    [GeneratedRegex(@"^\s*(?:StartupProject|Project)\s*=\s*(.+)$", Ic)] private static partial Regex VbgLine();
    [GeneratedRegex("^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"([^\"]+)\"\\s*,\\s*\"([^\"]+)\"", Cv | RegexOptions.Multiline)]
    private static partial Regex SlnProject();
    [GeneratedRegex(@"Worker|Web", Cv)] private static partial Regex WorkerOrWeb();
    [GeneratedRegex(@"\.(ps1|sql|js|ts)$", Ic)] private static partial Regex ExtraContent();
    [GeneratedRegex("^#\\s*TARGTYPE\\s+\"([^\"]+)\"", Cv | RegexOptions.Multiline)] private static partial Regex DspTarg();
    [GeneratedRegex("^#\\s*Name\\s+\"([^\"]+?)(?:\\s+-\\s+.*)?\"", Cv | RegexOptions.Multiline)] private static partial Regex DspName();
    [GeneratedRegex(@"^SOURCE=(.+)$", Cv | RegexOptions.Multiline)] private static partial Regex DspSource();
    [GeneratedRegex(@"[\\/]", Cv)] private static partial Regex Sep();

    public static FileFacts Parse(string path, string text)
    {
        string ext = ExtOf(path);
        string stem = StemOf(path);
        string fmt = ext.Length > 0 ? ext[1..] : "";
        try
        {
            switch (ext)
            {
                case ".vbp": return Proj(Vbp(stem, text));
                case ".vbg": return Sol(Vbg(stem, text));
                case ".sln": return Sol(Sln(stem, text));
                case ".slnx": return Sol(Slnx(stem, text));
                case ".dsp": return Proj(Dsp(stem, text));
                case ".csproj": case ".vbproj": case ".fsproj": case ".sqlproj": return Proj(MsBuild(stem, text, ext));
                case ".vcxproj": return Proj(Vcxproj(stem, text));
                case ".vcproj": return Proj(Vcproj(stem, text));
                case ".wixproj": return Proj(Wixproj(stem, text));
            }
        }
        catch (XmlException exc)
        {
            return Proj(new ProjectInfo { Format = fmt, Name = stem, Error = exc.Message });
        }
        return Proj(new ProjectInfo { Format = fmt, Name = stem });
    }

    private static FileFacts Proj(ProjectInfo p) => new() { Lang = "project", Project = p };
    private static FileFacts Sol(SolutionInfo s) => new() { Lang = "solution", Solution = s };

    private static string ExtOf(string path)
    {
        string name = Sep().Split(path)[^1];
        int i = name.LastIndexOf('.');
        return i > 0 ? name[i..].ToLowerInvariant() : "";
    }

    private static string StemOf(string path)
    {
        string name = Sep().Split(path)[^1];
        int i = name.LastIndexOf('.');
        return i > 0 ? name[..i] : name;
    }

    private static string[] SplitLines(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    private static string Unquote(string s) => s.Trim('"');

    // --------------------------------------------------------------------------- VB6

    private static ProjectInfo Vbp(string stem, string text)
    {
        var p = new ProjectInfo { Format = "vbp", Lang = "vb6", Name = stem };
        var ver = new Dictionary<string, string>();
        string? type = null;
        string? output = null;
        foreach (var raw in SplitLines(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string keyL = line[..eq].Trim().ToLowerInvariant();
            string val = line[(eq + 1)..].Trim();
            switch (keyL)
            {
                case "form": case "module": case "class": case "usercontrol": case "propertypage":
                case "designer": case "userdocument":
                {
                    // Module=Name; path   |  Form=path
                    int sc = val.IndexOf(';');
                    string member = sc >= 0 ? val[(sc + 1)..].Trim() : val;
                    p.Members.Add(Unquote(member));
                    break;
                }
                case "relateddoc": case "resfile32":
                    break;
                case "reference":
                {
                    // *\G{guid}#ver#lcid#path#Description   or  *\Arelative\path\Project.vbp
                    if (val.StartsWith("*\\A", StringComparison.Ordinal))
                    {
                        p.ProjectRefs.Add(val[3..]);
                        break;
                    }
                    var parts = val.Split('#');
                    string file = parts.Length > 3 ? parts[3] : "";
                    string desc = parts.Length > 4 ? parts[4] : "";
                    string fname = file.Length > 0 ? Sep().Split(file)[^1] : "";
                    p.Libs.Add(new LibRef { Kind = "com", File = fname, Name = desc.Length > 0 ? desc : fname, Path = file });
                    break;
                }
                case "object":
                {
                    // {guid}#ver#0; file.ocx
                    int sc = val.IndexOf(';');
                    string fname = sc >= 0 ? val[(sc + 1)..].Trim() : val;
                    p.Libs.Add(new LibRef { Kind = "ocx", File = fname, Name = fname });
                    break;
                }
                case "type": type = val; break;
                case "name": p.Name = Unquote(val); break;
                case "title": p.Title = Unquote(val); break;
                case "exename32": output = Unquote(val); break;
                case "majorver": case "minorver": case "revisionver": ver[keyL] = val; break;
                case "versioncomments": p.X["version_comments"] = Unquote(val); break;
            }
        }
        if (ver.Count > 0)
            p.Version = string.Join('.', new[] { "majorver", "minorver", "revisionver" }.Select(k => ver.GetValueOrDefault(k, "0")));
        string tl = (type ?? "").ToLowerInvariant();
        p.Output = !string.IsNullOrEmpty(output)
            ? output
            : p.Name + (tl switch { "exe" => ".exe", "oledll" => ".dll", "control" => ".ocx", _ => "" });
        p.Type = tl switch { "exe" => "exe", "oledll" => "dll", "control" => "ocx", "oleexe" => "exe", _ => type };
        return p;
    }

    private static SolutionInfo Vbg(string stem, string text)
    {
        var s = new SolutionInfo { Format = "vbg", Name = stem };
        foreach (var line in SplitLines(text))
        {
            var m = VbgLine().Match(line);
            if (m.Success) s.Projects.Add(Unquote(m.Groups[1].Value.Trim()));
        }
        return s;
    }

    // --------------------------------------------------------------------------- Visual Studio

    private static SolutionInfo Sln(string stem, string text)
    {
        var s = new SolutionInfo { Format = "sln", Name = stem };
        foreach (Match m in SlnProject().Matches(text))
        {
            string rel = m.Groups[2].Value;
            if (SolutionProjectExts.Contains(ExtOf(rel))) s.Projects.Add(rel);
        }
        return s;
    }

    /// <summary>.slnx: &lt;Solution&gt;&lt;Project Path="a\b.csproj"/&gt; (also inside &lt;Folder&gt;).</summary>
    private static SolutionInfo Slnx(string stem, string text)
    {
        var s = new SolutionInfo { Format = "slnx", Name = stem };
        var root = Xml(text);
        foreach (var el in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Project"))
        {
            string? rel = el.Attribute("Path")?.Value;
            if (!string.IsNullOrEmpty(rel) && SolutionProjectExts.Contains(ExtOf(rel))) s.Projects.Add(rel);
        }
        return s;
    }

    // --------------------------------------------------------------------------- XML helpers

    private static XElement Xml(string text)
    {
        text = text.TrimStart('\uFEFF');
        return XDocument.Parse(text).Root ?? throw new XmlException("no root element");
    }

    private static IEnumerable<XElement> Iter(XElement root, string tag) =>
        root.DescendantsAndSelf().Where(e => e.Name.LocalName == tag);

    private static string? Text(XElement el)
    {
        string t = string.Concat(el.Nodes().TakeWhile(n => n is XText).Select(n => ((XText)n).Value)).Trim();
        return t.Length > 0 ? t : null;
    }

    private static string? First(XElement root, string tag)
    {
        var el = root.Descendants().FirstOrDefault(e => e.Name.LocalName == tag);
        return el == null ? null : Text(el);
    }

    private static string? Attr(XElement el, string name)
    {
        string? v = el.Attribute(name)?.Value;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string Or(string? a, string b) => string.IsNullOrEmpty(a) ? b : a;

    private static ProjectInfo MsBuild(string stem, string text, string ext)
    {
        var root = Xml(text);
        var p = new ProjectInfo
        {
            Format = ext[1..],
            Lang = ext switch { ".csproj" => "csharp", ".vbproj" => "vbnet", ".fsproj" => "fsharp", _ => "sql" },
            Name = stem,
        };
        string? sdk = Attr(root, "Sdk");
        p.Assembly = Or(First(root, "AssemblyName"), stem);
        p.Name = ext != ".sqlproj" ? p.Assembly : Or(First(root, "Name"), stem);
        string defaultType = ext == ".sqlproj" ? "Database" : (sdk != null && WorkerOrWeb().IsMatch(sdk) ? "Exe" : "Library");
        string outType = Or(First(root, "OutputType"), defaultType).ToLowerInvariant();
        p.Type = outType switch { "winexe" or "exe" => "exe", "library" => "dll", "database" => "database", _ => outType };
        p.Output = p.Assembly + p.Type switch { "exe" => ".exe", "dll" => ".dll", "database" => ".dacpac", _ => "" };
        string? framework = First(root, "TargetFrameworkVersion") ?? First(root, "TargetFramework") ?? First(root, "TargetFrameworks");
        if (framework != null) p.X["framework"] = framework;
        string? v = First(root, "Version") ?? First(root, "AssemblyVersion") ?? First(root, "FileVersion");
        if (v != null) p.Version = v;
        string? rootNs = First(root, "RootNamespace");
        if (rootNs != null) p.X["root_namespace"] = rootNs;
        p.SdkStyle = sdk != null;
        foreach (var tag in new[] { "Compile", "Build", "Content", "None" })
            foreach (var el in Iter(root, tag))
            {
                string? inc = Attr(el, "Include");
                if (inc == null) continue;
                if (tag is "Content" or "None" && !ExtraContent().IsMatch(inc)) continue;
                p.Members.Add(inc);
            }
        foreach (var el in Iter(root, "ProjectReference"))
            if (Attr(el, "Include") is { } inc) p.ProjectRefs.Add(inc);
        foreach (var el in Iter(root, "Reference"))
        {
            string? inc = Attr(el, "Include");
            if (inc == null) continue;
            string name = inc.Split(',')[0].Trim();
            var hint = el.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath");
            p.Libs.Add(new LibRef { Kind = "assembly", Name = name, File = name + ".dll", Path = hint == null ? null : Text(hint) });
        }
        foreach (var el in Iter(root, "PackageReference"))
            if (Attr(el, "Include") is { } inc)
                p.Libs.Add(new LibRef { Kind = "package", Name = inc, Version = Attr(el, "Version") ?? First(el, "Version") });
        foreach (var el in Iter(root, "COMReference"))
            if (Attr(el, "Include") is { } inc)
                p.Libs.Add(new LibRef { Kind = "com", Name = inc, File = inc });
        return p;
    }

    private static string? NativeType(string? ctype) => ctype switch
    {
        "dynamiclibrary" => "dll",
        "application" => "exe",
        "staticlibrary" => "lib",
        null or "" => null,
        _ => ctype,
    };

    private static string BinExt(string? type) => type switch { "dll" => ".dll", "exe" => ".exe", "lib" => ".lib", _ => "" };

    private static ProjectInfo Vcxproj(string stem, string text)
    {
        var root = Xml(text);
        var p = new ProjectInfo
        {
            Format = "vcxproj", Lang = "cpp",
            Name = Or(First(root, "ProjectName") ?? First(root, "RootNamespace"), stem),
        };
        p.Type = NativeType((First(root, "ConfigurationType") ?? "").ToLowerInvariant());
        p.Output = Or(First(root, "TargetName"), stem) + BinExt(p.Type);
        foreach (var tag in new[] { "ClCompile", "ClInclude", "None", "ResourceCompile" })
            foreach (var el in Iter(root, tag))
                if (Attr(el, "Include") is { } inc) p.Members.Add(inc);
        foreach (var el in Iter(root, "ProjectReference"))
            if (Attr(el, "Include") is { } inc) p.ProjectRefs.Add(inc);
        foreach (var el in Iter(root, "AdditionalDependencies"))
            foreach (var part in (Text(el) ?? "").Split(';'))
            {
                string lib = part.Trim();
                if (lib.Length > 0 && !lib.StartsWith('%'))
                    p.Libs.Add(new LibRef { Kind = "native", Name = lib, File = lib });
            }
        return p;
    }

    private static ProjectInfo Vcproj(string stem, string text)
    {
        var root = Xml(text);
        var p = new ProjectInfo { Format = "vcproj", Lang = "cpp", Name = Or(Attr(root, "Name"), stem) };
        var cfg = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Configuration");
        string? ctype = cfg == null ? null : Attr(cfg, "ConfigurationType");
        p.Type = ctype switch { "2" => "dll", "1" => "exe", "4" => "lib", _ => null };
        p.Output = p.Name + BinExt(p.Type);
        foreach (var el in Iter(root, "File"))
            if (Attr(el, "RelativePath") is { } rp) p.Members.Add(rp);
        return p;
    }

    private static ProjectInfo Dsp(string stem, string text)
    {
        var p = new ProjectInfo { Format = "dsp", Lang = "cpp", Name = stem };
        var m = DspTarg().Match(text);
        if (m.Success)
        {
            string t = m.Groups[1].Value.ToLowerInvariant();
            p.Type = t.Contains("dynamic-link") ? "dll" : t.Contains("application") ? "exe" : t.Contains("static") ? "lib" : null;
        }
        m = DspName().Match(text);
        if (m.Success) p.Name = m.Groups[1].Value.Trim();
        p.Output = p.Name + BinExt(p.Type);
        foreach (Match sm in DspSource().Matches(text))
            p.Members.Add(Unquote(sm.Groups[1].Value.Trim()));
        return p;
    }

    private static ProjectInfo Wixproj(string stem, string text)
    {
        var root = Xml(text);
        string name = Or(First(root, "OutputName"), stem);
        string otype = Or(First(root, "OutputType"), "Package").ToLowerInvariant();
        string oext = otype switch { "package" => ".msi", "bundle" => ".exe", "module" => ".msm", "library" => ".wixlib", "patch" => ".msp", _ => "" };
        var p = new ProjectInfo
        {
            Format = "wixproj", Lang = "wix", Name = name,
            Type = otype is "package" or "bundle" ? "installer" : otype,
            Output = name + oext, SdkStyle = Attr(root, "Sdk") != null,
        };
        foreach (var el in Iter(root, "Compile"))
            if (Attr(el, "Include") is { } inc) p.Members.Add(inc);
        foreach (var el in Iter(root, "ProjectReference"))
            if (Attr(el, "Include") is { } inc) p.ProjectRefs.Add(inc);
        foreach (var el in Iter(root, "WixExtension"))
            if (Attr(el, "Include") is { } inc) p.Libs.Add(new LibRef { Kind = "wix extension", Name = inc, File = inc });
        foreach (var el in Iter(root, "PackageReference"))
            if (Attr(el, "Include") is { } inc) p.Libs.Add(new LibRef { Kind = "package", Name = inc, Version = Attr(el, "Version") });
        return p;
    }
}
