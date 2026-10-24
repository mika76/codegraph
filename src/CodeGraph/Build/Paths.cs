namespace CodeGraph.Build;

/// <summary>Path helpers with Windows semantics on every OS: '/' and '\' both separate, comparisons ignore case.</summary>
public static class Paths
{
    /// <summary>Case-insensitive normalised path key: forward slashes, '.' and '..' resolved, lower case.</summary>
    public static string Key(string path)
    {
        string p = path.Replace('\\', '/');
        string prefix = "";
        if (p.Length >= 2 && p[1] == ':') { prefix = p[..2]; p = p[2..]; }
        bool rooted = p.StartsWith('/');
        if (p.StartsWith("//")) { prefix += "/"; } // UNC: keep the double slash
        var stack = new List<string>();
        foreach (var part in p.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." && stack.Count > 0 && stack[^1] != "..") stack.RemoveAt(stack.Count - 1);
            else if (part == ".." && rooted) continue;
            else stack.Add(part);
        }
        string body = string.Join('/', stack);
        string result = prefix + (rooted ? "/" : "") + body;
        return (result.Length == 0 ? "." : result).ToLowerInvariant();
    }

    /// <summary>Join like os.path.join: a rooted second part replaces the first.</summary>
    public static string Join(string a, string b)
    {
        b = b.Replace('\\', '/');
        if (b.Length >= 2 && b[1] == ':' || a.Length == 0) return b;
        return a.TrimEnd('/', '\\') + "/" + b;
    }

    /// <summary>Directory part ('a/b/c.txt' -> 'a/b', 'c.txt' -> '').</summary>
    public static string Dir(string path)
    {
        int i = path.LastIndexOfAny(['/', '\\']);
        if (i < 0) return "";
        if (i == 0) return path[..1];
        if (i == 2 && path[1] == ':') return path[..3];
        return path[..i];
    }

    /// <summary>File name part.</summary>
    public static string Base(string path)
    {
        int i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Extension including the dot, lower case ('' when none; a leading dot is not an extension).</summary>
    public static string Ext(string path)
    {
        string b = Base(path);
        int leadingDots = b.Length - b.TrimStart('.').Length;
        int i = b.LastIndexOf('.');
        return i < leadingDots ? "" : b[i..].ToLowerInvariant();
    }

    /// <summary>Lower-case file name without directory and extension: 'C:\x\Orders.dll' -> 'orders'.</summary>
    public static string Stem(string? name)
    {
        string b = Base(name ?? "");
        string ext = Ext(b);
        return (ext.Length > 0 ? b[..^ext.Length] : b).ToLowerInvariant();
    }
}
