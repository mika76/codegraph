using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CodeGraph.Model;

// What a parser found in one file. Common fields are typed; anything parser-specific that only one
// language has (C# usings, JS imports, WiX services ...) goes into Extra (serialised as "extra").

/// <summary>Everything one parser found in one file.</summary>
public sealed class FileFacts
{
    public string Lang { get; set; } = "unknown";
    public List<Symbol> Symbols { get; set; } = [];
    public List<Ref> Refs { get; set; } = [];
    public int Loc { get; set; }
    public ProjectInfo? Project { get; set; }
    public SolutionInfo? Solution { get; set; }
    public string? Error { get; set; }

    public JsonObject? Extra { get; set; }

    [JsonIgnore] public JsonObject X => Extra ??= [];
}

/// <summary>A definition: type, routine, property, table, column, route, installer feature...</summary>
public sealed class Symbol
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Line { get; set; }
    /// <summary>Containing type (class, module, form, table...) or null for top level.</summary>
    public string? Parent { get; set; }
    public string? Visibility { get; set; }
    /// <summary>Number of lines the definition spans.</summary>
    public int Lines { get; set; }
    public string? Signature { get; set; }

    public JsonObject? Extra { get; set; }

    [JsonIgnore] public JsonObject X => Extra ??= [];
}

/// <summary>A use of something by name, resolved later by the builder.</summary>
public sealed class Ref
{
    /// <summary>Routine the reference is in (null at file/type level).</summary>
    public string? From { get; set; }
    /// <summary>Type the reference is in.</summary>
    public string? FromParent { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public int Line { get; set; }
    /// <summary>Qualifier for member calls: <c>obj.Method</c> -> "obj".</summary>
    public string? Qual { get; set; }
    /// <summary>Declared type of the qualifier, when the parser knows it.</summary>
    public string? QualType { get; set; }
    /// <summary>SQL access mode: read, write, exec, call.</summary>
    public string? Mode { get; set; }
    public string? Via { get; set; }

    public JsonObject? Extra { get; set; }

    [JsonIgnore] public JsonObject X => Extra ??= [];
}

/// <summary>A project file (.vbp, .csproj, .vcxproj, .wixproj, package.json ...).</summary>
public sealed class ProjectInfo
{
    public string Format { get; set; } = "";
    public string? Lang { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>exe, dll, ocx, lib, installer, database, npm package ...</summary>
    public string? Type { get; set; }
    /// <summary>Output file name, e.g. Orders.dll.</summary>
    public string? Output { get; set; }
    public string? Assembly { get; set; }
    public string? Version { get; set; }
    /// <summary>Member source files, relative to the project file as written in it.</summary>
    public List<string> Members { get; set; } = [];
    public List<LibRef> Libs { get; set; } = [];
    /// <summary>Referenced project files, relative to the project file.</summary>
    public List<string> ProjectRefs { get; set; } = [];
    public bool SdkStyle { get; set; }
    public string? Error { get; set; }

    public JsonObject? Extra { get; set; }

    [JsonIgnore] public JsonObject X => Extra ??= [];
}

/// <summary>A library a project uses: COM, OCX, assembly, NuGet/npm package, native lib, WiX extension.</summary>
public sealed class LibRef
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? File { get; set; }
    public string? Path { get; set; }
    public string? Version { get; set; }
}

/// <summary>A solution / group file (.sln, .slnx, .vbg) listing project files.</summary>
public sealed class SolutionInfo
{
    public string Format { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Projects { get; set; } = [];
}
