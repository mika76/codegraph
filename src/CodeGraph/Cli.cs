namespace CodeGraph;

/// <summary>Command line: codegraph build | show | impact | path | release-snapshot | init.</summary>
public static class Cli
{
    private const string Usage = """
        codegraph - a knowledge graph of your code (VB6, C#, C/C++, SQL, PowerShell, TS/JS/Vue, WiX) and an Obsidian vault.

        Usage:  codegraph build [--full] [--no-vault]    scan repos, write graph.json, report and vault
                codegraph show <name>                    a project/file/routine/DB object and its connections
                codegraph impact <name> [--depth N]      what depends on it (directly or indirectly)
                codegraph path <a> <b>                   how two things are connected
                codegraph release-snapshot <ver> [--date yyyy-mm-dd]
                                                         add a release with current project versions to releases.json
                codegraph init                           write example codegraph.json, releases.json, knowledge.json here

        Options: -c, --config <file>   config file (default: codegraph.json in the current folder)
        """;

    public static int Run(string[] argv)
    {
        var args = new List<string>(argv);
        string? configPath = TakeOption(args, "-c", "--config");
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
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
            case "show":
                return Commands.Show(cfg, Single(args, "name"));
            case "impact":
                int depth = int.TryParse(TakeOption(args, "--depth"), out var d) ? d : 3;
                return Commands.Impact(cfg, Single(args, "name"), depth);
            case "path":
                if (args.Count != 2) throw new CodeGraphException("Usage: codegraph path <a> <b>");
                return Commands.Path(cfg, args[0], args[1]);
            case "release-snapshot":
                string? date = TakeOption(args, "--date");
                return Commands.ReleaseSnapshot(cfg, Single(args, "version"), date);
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

    private static string Single(List<string> args, string what)
    {
        if (args.Count != 1) throw new CodeGraphException($"Expected one {what}, got {args.Count}.");
        return args[0];
    }

    private static void ExpectNoMore(List<string> args)
    {
        if (args.Count > 0) throw new CodeGraphException($"Unexpected argument(s): {string.Join(" ", args)}");
    }
}
