using Argus.Cli;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Packs;
using Argus.Packs.Sources;

namespace Argus;

public static class Program
{
    const string Prog = "argus";

    static readonly string[] Commands_ =
        ["embed", "index", "backup", "kpi", "status", "verify", "resolve", "serve", "flush-acl", "pack", "healthcheck"];

    static readonly string[] PackCommands = ["build", "list", "install", "info", "remove", "update", "index"];

    static ArgSpec Where(ArgSpec spec) =>
        spec.Opt("--config", "Read packs.dir from this config").Opt("--packs-dir", "Directory holding installed packs");

    static ArgSpec SpecFor(string command) => command switch
    {
        "embed" => new ArgSpec($"{Prog} embed").Opt("--config", required: true)
            .Opt("--limit", "Embed at most this many symbols, then stop. For a first run on a large corpus, to see the rate before committing to hours."),
        "index" => new ArgSpec($"{Prog} index").Opt("--config", required: true)
            .Opt("--repo", "Index only this path_with_namespace")
            .Flag("--allow-partial-enumeration", "Index even when the service token cannot see every repository.")
            .Opt("--interval", "Keep running, starting a new pass every SECONDS.", metavar: "SECONDS")
            .Many("--branch", "Index this branch in every repo, in addition to each default branch. Repeatable, and a glob.", metavar: "GLOB")
            .Flag("--reset-retries", "Clear retry counters before indexing (manual recovery only; do not use on a schedule)"),
        "backup" => new ArgSpec($"{Prog} backup").Opt("--config", required: true).Opt("--out", "Directory to write the snapshot into", required: true),
        "kpi" => new ArgSpec($"{Prog} kpi").Opt("--config", required: true).Flag("--json", "Emit one JSON object, for appending to a time series"),
        "status" => new ArgSpec($"{Prog} status").Opt("--config", required: true),
        "verify" => new ArgSpec($"{Prog} verify").Opt("--config", required: true)
            .Opt("--text-file", "File holding the draft. Use - to read stdin (what a hook pipes).")
            .Opt("--text", "The draft inline, for a quick check")
            .Opt("--limit", "How many identifiers to resolve (default 40)")
            .Flag("--json", "Emit the findings as JSON on stdout")
            .Flag("--quiet", "Say nothing when the draft is clean"),
        "resolve" => new ArgSpec($"{Prog} resolve").Opt("--config", required: true),
        "serve" => new ArgSpec($"{Prog} serve").Opt("--config", required: true)
            .Flag("--stdio", "Serve MCP over stdin/stdout instead of HTTP. Credential comes from ARGUS_TOKEN.")
            .Opt("--host", $"Bind address (default: {Cli.Commands.DefaultServeHost})")
            .Opt("--port", $"Bind port (default: {Cli.Commands.DefaultServePort})")
            .Many("--allowed-host", "Host header value the DNS-rebinding check will accept on /mcp (repeatable).", dest: "allowed_hosts", metavar: "HOST"),
        "healthcheck" => new ArgSpec($"{Prog} healthcheck").Opt("--url", "Health endpoint (default: http://127.0.0.1:7700/healthz)"),
        "flush-acl" => new ArgSpec($"{Prog} flush-acl").Opt("--config", required: true).Opt("--user", "Only clear this GitLab username's cache entries"),
        "pack build" => new ArgSpec($"{Prog} pack build")
            .Opt("--source", $"One of: {string.Join(", ", SourceCatalog.Sources.Keys.OrderBy(k => k, StringComparer.Ordinal))}", required: true)
            .Opt("--work-dir", "Checkout of the source repository", required: true)
            .Opt("--out", "Pack file to write", required: true)
            .Opt("--version", "Version to record in the pack", required: true)
            .Opt("--commit", "Source commit, if work-dir is not a git checkout")
            .Flag("--fetch", "Clone or update the source into --work-dir first"),
        "pack list" => Where(new ArgSpec($"{Prog} pack list")),
        "pack install" => Where(new ArgSpec($"{Prog} pack install").Positional("source", help: "Pack file path or https URL")
            .Opt("--sha256", "Expected SHA-256; install is refused on mismatch")),
        "pack info" => Where(new ArgSpec($"{Prog} pack info").Positional("name")),
        "pack remove" => Where(new ArgSpec($"{Prog} pack remove").Positional("name")),
        "pack update" => Where(new ArgSpec($"{Prog} pack update").Positional("name", optional: true, help: "Only this pack (default: all)")
            .Opt("--index-url", "Published pack index JSON", required: true)),
        "pack index" => Where(new ArgSpec($"{Prog} pack index").Opt("--out", "Index JSON file to write", required: true)
            .Opt("--base-url", "Where the packs will be SERVED from.", required: true)
            .Positional("name", optional: true, help: "Only this pack (default: all)")),
        _ => throw new UsageError($"argument command: invalid choice: '{command}'"),
    };

    static string TopUsage() => $"usage: {Prog} [-h] {{{string.Join(",", Commands_)}}} ...";

    public static int Main(string[] args)
    {
        // git runs this same executable as its askpass helper on Windows.
        if (Environment.GetEnvironmentVariable(Mirror.AskpassModeEnv) == "1") return Mirror.AnswerAskpass(args);
        Console.Out.Flush();
        try
        {
            return Run(args);
        }
        catch (HelpRequested help)
        {
            Console.Out.WriteLine(help.Message);
            return 0;
        }
    }

    public static int Run(string[] args)
    {
        string command = "";
        ArgSpec? spec = null;
        Parsed parsed;
        try
        {
            if (args.Length == 0) throw new UsageError("the following arguments are required: command");
            if (args[0] is "-h" or "--help") throw new HelpRequested(TopUsage() + "\n\nArgus: a self-hosted code index and documentation MCP server.\n\ncommands: " + string.Join(", ", Commands_));
            command = args[0];
            if (!Commands_.Contains(command))
                throw new UsageError($"argument command: invalid choice: '{command}' (choose from {string.Join(", ", Commands_.Select(c => $"'{c}'"))})");
            var rest = args.Skip(1).ToList();
            if (command == "pack")
            {
                if (rest.Count == 0) throw new UsageError("the following arguments are required: pack_command");
                if (rest[0] is "-h" or "--help") throw new HelpRequested($"usage: {Prog} pack [-h] {{{string.Join(",", PackCommands)}}} ...");
                if (!PackCommands.Contains(rest[0]))
                    throw new UsageError($"argument pack_command: invalid choice: '{rest[0]}' (choose from {string.Join(", ", PackCommands.Select(c => $"'{c}'"))})");
                command = $"pack {rest[0]}";
                rest = rest.Skip(1).ToList();
            }
            spec = SpecFor(command);
            parsed = spec.Parse(rest);
        }
        catch (UsageError exc)
        {
            Console.Error.WriteLine(spec?.Usage() ?? TopUsage());
            Console.Error.WriteLine($"{spec?.Prog ?? Prog}: error: {exc.Message}");
            return 2;
        }

        try
        {
            return Dispatch(command, parsed);
        }
        catch (UsageError exc)
        {
            Console.Error.WriteLine(spec!.Usage());
            Console.Error.WriteLine($"{spec.Prog}: error: {exc.Message}");
            return 2;
        }
    }

    static int Dispatch(string command, Parsed a)
    {
        if (command.StartsWith("pack ", StringComparison.Ordinal))
        {
            try
            {
                return command switch
                {
                    "pack build" => Cli.Commands.PackBuild(a),
                    "pack list" => Cli.Commands.PackList(a),
                    "pack install" => Cli.Commands.PackInstall(a),
                    "pack info" => Cli.Commands.PackInfo(a),
                    "pack remove" => Cli.Commands.PackRemove(a),
                    "pack update" => Cli.Commands.PackUpdate(a),
                    _ => Cli.Commands.PackIndex(a),
                };
            }
            catch (ConfigError exc)
            {
                Console.Error.WriteLine($"config error: {exc.Message}");
                return 2;
            }
            catch (Exception exc) when (exc is BuildError or RegistryError or GitError or InventoryError)
            {
                Console.Error.WriteLine($"pack error: {exc.Message}");
                return Cli.Commands.ExitPack;
            }
        }
        if (command == "verify") return Cli.Commands.Verify(a);
        if (command == "healthcheck") return Cli.Commands.Healthcheck(a.Get("url") ?? "http://127.0.0.1:7700/healthz");

        ArgusConfig cfg;
        try { cfg = ArgusConfig.Load(a.Req("config")); }
        catch (Exception exc) when (exc is ConfigError or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"config error: {exc.Message}");
            return 2;
        }

        try
        {
            switch (command)
            {
                case "index":
                    if (a.List("branch") is { Count: > 0 } branches) cfg = cfg with { Index = cfg.Index with { Branches = branches } };
                    var interval = a.Int("interval") ?? 0;
                    if (interval > 0)
                        return Cli.Commands.IndexRepeatedly(cfg, a.Get("repo"), a.Flag("reset_retries"), a.Flag("allow_partial_enumeration"), interval);
                    return Cli.Commands.Index(cfg, a.Get("repo"), a.Flag("reset_retries"), a.Flag("allow_partial_enumeration"));
                case "embed":
                    return Cli.Commands.EmbedCommand(cfg, a.Int("limit"));
                case "serve":
                    if (a.Flag("stdio")) return Cli.Commands.ServeStdio(cfg);
                    return Cli.Commands.Serve(cfg, a.Get("host") ?? Cli.Commands.DefaultServeHost, a.Int("port") ?? Cli.Commands.DefaultServePort,
                        a.List("allowed_hosts"));
                case "flush-acl":
                    return Cli.Commands.FlushAcl(cfg, a.Get("user"));
                case "kpi":
                    return Cli.Commands.KpiCommand(cfg, a.Flag("json"));
                case "backup":
                    return Cli.Commands.Backup(cfg, a.Req("out"), a.Get("config"));
                case "resolve":
                    return Cli.Commands.ResolveCommand(cfg);
                default:
                    return Cli.Commands.Status(cfg);
            }
        }
        catch (GitLabError exc)
        {
            Console.Error.WriteLine($"gitlab error: {exc.Message}");
            return 3;
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException or CredentialError)
        {
            Console.Error.WriteLine($"could not reach GitLab: {exc.GetType().Name}: {exc.Message}");
            return 3;
        }
    }
}
