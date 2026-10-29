namespace CodeGraph;

/// <summary>Command line: codegraph build | show | impact | path | db-usage | mcp | release-snapshot | init.</summary>
public static class Cli
{
    private const string Usage = """
        codegraph - a knowledge graph of your code (VB6, C#, C/C++, SQL, PowerShell, TS/JS/Vue, WiX) and an Obsidian vault.

        Usage:  codegraph build [--full] [--no-vault]    scan repos, write graph.json, report and vault
                codegraph search <text> [--kind K] [--repo R] [--limit N]
                                                         find nodes by name, path or id (prints their ids)
                codegraph show <name> [--relation R] [--direction in|out|both] [--limit N] [--ids]
                                                         a project/file/routine/DB object and its connections
                codegraph impact <name> [--depth N] [--ids]
                                                         what depends on it (directly or indirectly)
                codegraph path <a> <b> [--max-len N]     how two things are connected (chains up to N steps)
                codegraph db-usage <name> [--depth N] [--ids]
                                                         who reads/writes a table, column or procedure, up to the screens
                codegraph mcp                            serve the graph to AI assistants (MCP over stdio)
                codegraph release-snapshot <ver> [--date yyyy-mm-dd]
                                                         add a release with current project versions to releases.json
                codegraph init                           write example codegraph.json, releases.json, knowledge.json here

        Options: -c, --config <file>   config file (default: codegraph.json in the current folder)
                 --version             print the version
        """;

    /// <summary>Informational version without the "+commit" suffix the SDK appends.</summary>
    public static string Version =>
        (typeof(Cli).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0]
        ?? "dev";

    public static int Run(string[] argv)
    {
        var args = new List<string>(argv);
        string? configPath = TakeOption(args, "-c", "--config");
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args[0] is "--version" or "version")
        {
            Console.WriteLine($"codegraph {Version}");
            return 0;
        }
        string cmd = args[0];
        args.RemoveAt(0);
        if (cmd == "init")
            return Init();

        configPath ??= "codegraph.json";
        if (!File.Exists(configPath))
            throw new CodeGraphException($"Config not found: {configPath}  (create one with:  codegraph init)");
        var cfg = Config.Load(configPath);

        switch (cmd)
        {
            case "build":
                bool full = TakeFlag(args, "--full"), noVault = TakeFlag(args, "--no-vault");
                ExpectNoMore(args);
                return Commands.Build(cfg, full, noVault);
            case "search":
            {
                string? kind = TakeOption(args, "--kind"), repo = TakeOption(args, "--repo");
                int limit = Number(TakeOption(args, "--limit"), "--limit", 25, 1);
                return Commands.Search(cfg, Single(args, "search text", cmd), kind, repo, limit);
            }
            case "show":
            {
                string? relation = TakeOption(args, "--relation");
                string direction = (TakeOption(args, "--direction") ?? "both").ToLowerInvariant();
                if (direction is not ("in" or "out" or "both"))
                    throw new CodeGraphException($"--direction must be in, out or both, not '{direction}'.");
                int limit = Number(TakeOption(args, "--limit"), "--limit", 40, 1);
                bool ids = TakeFlag(args, "--ids");
                return Commands.Show(cfg, Single(args, "name", cmd), relation, direction, limit, ids);
            }
            case "impact":
            {
                int depth = Number(TakeOption(args, "--depth"), "--depth", 3, 1);
                bool ids = TakeFlag(args, "--ids");
                return Commands.Impact(cfg, Single(args, "name", cmd), depth, ids);
            }
            case "path":
                int maxLen = Number(TakeOption(args, "--max-len"), "--max-len", 8, 1);
                RejectOptions(args, cmd);
                if (args.Count != 2) throw new CodeGraphException("Usage: codegraph path <a> <b> [--max-len N]");
                return Commands.Path(cfg, args[0], args[1], maxLen);
            case "db-usage":
            {
                int depth = Number(TakeOption(args, "--depth"), "--depth", 4, 0);
                bool ids = TakeFlag(args, "--ids");
                return Commands.DbUsage(cfg, Single(args, "name", cmd), depth, ids);
            }
            case "mcp":
                ExpectNoMore(args);
                return Commands.Mcp(cfg);
            case "release-snapshot":
                string? date = TakeOption(args, "--date");
                return Commands.ReleaseSnapshot(cfg, Single(args, "version", cmd), date);
            default:
                throw new CodeGraphException($"Unknown command: {cmd}\n\n{Usage}");
        }
    }

    private static int Init()
    {
        string examples = Path.Combine(AppContext.BaseDirectory, "Examples");
        foreach (var name in new[] { "codegraph.json", "releases.json", "knowledge.json" })
        {
            if (File.Exists(name))
                Console.WriteLine($"  {name} already exists, left as is");
            else
            {
                File.Copy(Path.Combine(examples, name), name);
                Console.WriteLine($"  wrote {name}");
            }
        }
        return 0;
    }

    private static string? TakeOption(List<string> args, params string[] names)
    {
        int i = args.FindIndex(a => names.Contains(a));
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new CodeGraphException($"{args[i]} needs a value");
        string v = args[i + 1];
        args.RemoveRange(i, 2);
        return v;
    }

    private static bool TakeFlag(List<string> args, string name) => args.Remove(name);

    /// <summary>A whole-number option value of at least <paramref name="min"/>, or the default when not given.</summary>
    private static int Number(string? value, string option, int fallback, int min)
    {
        if (value is null) return fallback;
        if (!int.TryParse(value, out int n) || n < min)
            throw new CodeGraphException($"{option} needs a whole number of at least {min}, not '{value}'.");
        return n;
    }

    /// <summary>Options left after a command took its own are not names: say so instead of miscounting names.</summary>
    private static void RejectOptions(List<string> args, string cmd)
    {
        var unknown = args.Where(a => a.Length > 1 && a[0] == '-' && !char.IsDigit(a[1])).ToList();
        if (unknown.Count > 0)
            throw new CodeGraphException($"Unknown option{(unknown.Count == 1 ? "" : "s")} for {cmd}: {string.Join(" ", unknown)}  " +
                                         "(see codegraph --help)");
    }

    private static string Single(List<string> args, string what, string cmd)
    {
        RejectOptions(args, cmd);
        if (args.Count != 1)
            throw new CodeGraphException($"Expected one {what}, got {args.Count}" +
                                         (args.Count > 1 ? $": {string.Join(", ", args.Select(a => $"\"{a}\""))} (quote a name with spaces)." : "."));
        return args[0];
    }

    private static void ExpectNoMore(List<string> args)
    {
        if (args.Count > 0) throw new CodeGraphException($"Unexpected argument(s): {string.Join(" ", args)}");
    }
}
