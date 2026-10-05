using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>The command line, parsed.</summary>
internal sealed class Options
{
    /// <summary>login, logout, models, web, chat, help, version; null: the IDE without a prompt, the agent in this terminal with one.</summary>
    public string? Command { get; set; }
    public string? Prompt { get; set; }
    /// <summary>One-shot: answer the prompt, print the answer, exit.</summary>
    public bool Print { get; set; }
    public string Output { get; set; } = "text";
    public bool Continue { get; set; }
    public bool Resume { get; set; }
    public string? ResumeId { get; set; }
    public string? Model { get; set; }
    public string? Mode { get; set; }
    public string? Thinking { get; set; }
    public string? Ca { get; set; }
    public List<string> AddDirs { get; } = [];
    public string? Url { get; set; }
    public string? Gateway { get; set; }
    public bool NoColor { get; set; }
    /// <summary>The IDE: the port (null: a free one), and whether to leave the browser closed.</summary>
    public int? Port { get; set; }
    public bool NoOpen { get; set; }
}

/// <summary>What the program runs with: its streams, environment, folder and paths. Tests make their own.</summary>
internal sealed class CliEnv
{
    public required TextReader In { get; init; }
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    public required Func<string, string?> Env { get; init; }
    public required string Cwd { get; init; }
    public required AppPaths Paths { get; init; }
    /// <summary>stdin is a terminal: questions can be asked.</summary>
    public bool InTerminal { get; init; }
    public bool OutTerminal { get; init; }
    public bool ErrTerminal { get; init; }
    public Func<string, string?>? ReadSecret { get; init; }
    public CancelKey Cancel { get; } = new();
    /// <summary>The web interface's page; null: the one built into the program.</summary>
    public WebAssets? Web { get; init; }
}

/// <summary>The command line: code-arena (the IDE), chat, [options] prompt, login, logout, models.</summary>
internal static partial class Cli
{
    public static string Version { get; } =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The licence this version is offered under (LICENSING.md: or a commercial license).</summary>
    public const string License = "AGPL-3.0-only";

    /// <summary>
    /// Where this version's complete source is: --version and the IDE's about box
    /// show it, as LICENSING.md's additional terms ask. A modified version points
    /// it at its own source.
    /// </summary>
    public const string Source = "https://github.com/Binchitects/argus";

    /// <summary>What --version prints: the version, the licence and the source.</summary>
    public static string About => $"""
        code-arena {Version}
        Copyright (C) 2026 Binchitects and contributors. There is no warranty.
        Licence: {License}, with the additional terms in LICENSING.md (or a commercial license from Binchitects).
        Source: {Source}
        """;

    public const string Help = """
        Code Arena: an IDE in your browser around a coding agent, on your company's Argus Arena.

        Usage:
          code-arena [options]              the IDE for this folder, in your browser, on this machine only
          code-arena web [options]          the same
          code-arena chat [prompt]          the agent in this terminal instead
          code-arena [options] "prompt"     a conversation in this terminal that starts with the prompt
          code-arena -p "prompt"            answer once and print the answer (-p - reads the prompt from stdin)
          code-arena login                  sign in: the Arena's address and your API key
          code-arena logout                 forget the API key
          code-arena models                 the models you may use

        Options:
          -p, --print [prompt]       one-shot, for scripts; --output json for a JSON answer
          -c, --continue             carry on with this folder's last session
          -r, --resume [id]          pick a saved session (or give its id)
          -m, --model NAME           the model (see code-arena models)
              --mode MODE            ask (default), auto-edit, plan or yolo
              --thinking LEVEL       off, low, medium, high, xhigh, or default
              --add-dir DIR          let the tools use another folder too (repeatable)
              --ca FILE              trust this CA certificate (PEM) besides the system's
              --url URL              login: the Arena's address, https://DOMAIN
              --gateway URL          login: the gateway, when it is not https://gateway.DOMAIN
              --no-color             plain text
              --port N               the IDE: listen on this port (default: a free one)
              --no-open              the IDE: print the address, do not open the browser
          -v, --version
          -h, --help

        In the terminal, /help lists the commands. Ctrl+C stops a turn; twice at the prompt leaves.
        Settings: code-arena keeps them in config.json in its config folder (see docs/code-arena.md).
        """;

    public static async Task<int> RunAsync(string[] args, CliEnv env, CancellationToken ct = default)
    {
        Options o;
        try
        {
            o = Parse(args);
        }
        catch (ArgumentException e)
        {
            env.Err.WriteLine($"code-arena: {e.Message} (code-arena --help)");
            return 2;
        }
        switch (o.Command)
        {
            case "help":
                env.Out.WriteLine(Help);
                return 0;
            case "version":
                env.Out.WriteLine(About);
                return 0;
            case "login":
                return await LoginAsync(o, env, MakeUi(env, o, quiet: false), ct);
            case "logout":
                return Logout(env, MakeUi(env, o, quiet: false));
            case "models":
                return await ModelsAsync(o, env, MakeUi(env, o, quiet: false), ct);
            case "web":
                return await WebAsync(o, env, ct);
            case "chat":
                return await AgentAsync(o, env, ct);
            default:
                // Without a prompt, the IDE; a prompt (or -p) starts the conversation in this terminal.
                return o.Prompt is null && !o.Print ? await WebAsync(o, env, ct) : await AgentAsync(o, env, ct);
        }
    }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var words = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            bool NextIsValue() => i + 1 < args.Length && !args[i + 1].StartsWith('-');
            if (i == 0 && a is "login" or "logout" or "models" or "web" or "chat" or "help" or "version")
            {
                o.Command = a;
                continue;
            }
            switch (a)
            {
                case "-h" or "--help":
                    o.Command = "help";
                    break;
                case "-v" or "--version":
                    o.Command = "version";
                    break;
                case "-p" or "--print":
                    o.Print = true;
                    if (i + 1 < args.Length && (args[i + 1] == "-" || !args[i + 1].StartsWith('-')))
                    {
                        o.Prompt = args[++i];
                    }
                    break;
                case "--output" or "--output-format":
                    o.Output = Value() is var f && f is "text" or "json" ? f : throw new ArgumentException("--output is text or json.");
                    break;
                case "-c" or "--continue":
                    o.Continue = true;
                    break;
                case "-r" or "--resume":
                    o.Resume = true;
                    if (NextIsValue() && SessionId().IsMatch(args[i + 1]))
                    {
                        o.ResumeId = args[++i];
                    }
                    break;
                case "-m" or "--model":
                    o.Model = Value();
                    break;
                case "--mode" or "--permission-mode":
                    o.Mode = Value();
                    break;
                case "--thinking":
                    o.Thinking = Value() is var t && (t == "default" || ModelState.Levels.Contains(t)) ? (t == "default" ? null : t)
                        : throw new ArgumentException($"--thinking is one of: default, {string.Join(", ", ModelState.Levels)}.");
                    break;
                case "--ca":
                    o.Ca = Value();
                    break;
                case "--add-dir":
                    o.AddDirs.Add(Value());
                    break;
                case "--url":
                    o.Url = Value();
                    break;
                case "--gateway":
                    o.Gateway = Value();
                    break;
                case "--no-color":
                    o.NoColor = true;
                    break;
                case "--port":
                    o.Port = int.TryParse(Value(), out var port) && port is >= 1 and <= 65535 ? port : throw new ArgumentException("--port is a number from 1 to 65535.");
                    break;
                case "--no-open":
                    o.NoOpen = true;
                    break;
                case "--":
                    words.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default:
                    if (a.StartsWith('-') && a != "-")
                    {
                        throw new ArgumentException($"unknown option {a}");
                    }
                    words.Add(a);
                    break;
            }
        }
        if (words.Count > 0)
        {
            o.Prompt = o.Prompt is null ? string.Join(' ', words) : o.Prompt + " " + string.Join(' ', words);
        }
        if (o.Ca is { } ca)
        {
            o.Ca = Path.GetFullPath(ca);
        }
        if (o.Command == "web" && (o.Prompt is not null || o.Print))
        {
            throw new ArgumentException("web takes no prompt: write it in the IDE's chat (or use code-arena chat \"prompt\").");
        }
        return o;
    }

    [GeneratedRegex(@"^\d{8}(-\d{0,6}(-[0-9a-f]{0,6})?)?$")]
    private static partial Regex SessionId();

    private static Ui MakeUi(CliEnv env, Options o, bool quiet)
    {
        var plain = o.NoColor || env.Env("NO_COLOR") is { Length: > 0 } || env.Env("TERM") == "dumb";
        var color = !plain && (quiet ? env.ErrTerminal : env.OutTerminal);
        var ui = new Ui(env.In, env.Out, env.Err, color, canAsk: env.InTerminal && !quiet)
        {
            Quiet = quiet,
            Animate = color && env.ErrTerminal,
            SecretReader = env.ReadSecret,
        };
        env.Cancel.Ui = ui;
        return ui;
    }

    private static Config? LoadConfig(CliEnv env, Ui ui)
    {
        try
        {
            return Config.Load(env.Paths.ConfigFile);
        }
        catch (InvalidOperationException e)
        {
            ui.Error(e.Message);
            return null;
        }
    }

    /// <summary>Asks for the address and the key, checks them with the gateway, and saves them (the key never shown).</summary>
    private static async Task<int> LoginAsync(Options o, CliEnv env, Ui ui, CancellationToken ct)
    {
        if (LoadConfig(env, ui) is not { } config)
        {
            return 1;
        }
        var url = o.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            var answer = ui.ReadLine($"Your Arena's address{(config.Url is { } known ? $" [{known}]" : " (https://DOMAIN)")}: ")?.Trim();
            url = string.IsNullOrEmpty(answer) ? config.Url : answer;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            ui.Error("The Arena's address is needed: code-arena login --url https://DOMAIN");
            return 2;
        }
        url = Config.NormalizeUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            ui.Error($"{url} is not a web address (https://DOMAIN).");
            return 2;
        }
        if (uri.Scheme == "http")
        {
            ui.Warn("http: your key would travel unencrypted. Use the https address unless this is a test.");
        }
        var sameArena = string.Equals(url, config.Url, StringComparison.OrdinalIgnoreCase);
        var gateway = o.Gateway is { Length: > 0 } g ? Config.NormalizeUrl(g) : sameArena ? config.Gateway : null;
        var ca = o.Ca ?? (sameArena ? config.Ca : null);

        string? key;
        if (env.Env("ARENA_API_KEY") is { Length: > 0 } fromEnv)
        {
            ui.Info("Using the key in ARENA_API_KEY.");
            key = fromEnv;
        }
        else
        {
            key = ui.ReadSecret("Your API key (in Arena: Your account → API key): ")?.Trim();
        }
        if (string.IsNullOrEmpty(key))
        {
            ui.Error("No key given.");
            return 2;
        }

        var check = new Config { Url = url, Gateway = gateway, ApiKey = key };
        HttpClient http;
        try
        {
            http = Net.Client(Net.CaFile(ca, null, env.Env));
        }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException)
        {
            ui.Error(e.Message);
            return 1;
        }
        using (http)
        {
            var gatewayUrl = check.GatewayUrl!;
            try
            {
                var models = await new GatewayClient(http, gatewayUrl, key).ModelIdsAsync(ct);
                ui.Line($"{ui.Green("✓")} The gateway ({gatewayUrl}) takes your key: {models.Count} model{(models.Count == 1 ? "" : "s")}" +
                        (models.Count > 0 ? $" ({string.Join(", ", models.Take(4))}{(models.Count > 4 ? ", …" : "")})." : "."));
            }
            catch (Exception e) when (e is GatewayException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                ui.Error(e is TaskCanceledException ? $"{gatewayUrl} did not answer in time." : e.Message);
                return 1;
            }
            if (config.ArenaTools && check.ArenaMcpUrl is { } mcpUrl)
            {
                try
                {
                    await using var arena = await McpClient.ConnectAsync("arena", new HttpMcpTransport(http, mcpUrl,
                        new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }), ct);
                    ui.Line($"{ui.Green("✓")} Arena's tools: {arena.Tools.Count} ({string.Join(", ", arena.Tools.Take(6).Select(t => t.Str("name")))}{(arena.Tools.Count > 6 ? ", …" : "")}).");
                }
                catch (McpUnavailableException)
                {
                    ui.Info($"Arena's tools are not available on this Arena (no MCP endpoint at {mcpUrl}): sessions use the local tools.");
                }
                catch (Exception e) when (e is McpException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    ui.Warn($"Arena's tools did not connect: {e.Message}");
                }
            }
        }
        config.Url = url;
        config.Gateway = gateway;
        config.ApiKey = key;
        config.Ca = ca;
        config.Save(env.Paths.ConfigFile);
        ui.Line($"Signed in to {url}. Saved in {env.Paths.ConfigFile} (only you can read it).");
        return 0;
    }

    private static int Logout(CliEnv env, Ui ui)
    {
        if (LoadConfig(env, ui) is not { } config)
        {
            return 1;
        }
        if (config.ApiKey is null)
        {
            ui.Line("Not signed in.");
            return 0;
        }
        config.ApiKey = null;
        config.Save(env.Paths.ConfigFile);
        ui.Line($"Signed out: the key is gone from {env.Paths.ConfigFile}.");
        return 0;
    }

    private static async Task<int> ModelsAsync(Options o, CliEnv env, Ui ui, CancellationToken ct)
    {
        if (LoadConfig(env, ui) is not { } config)
        {
            return 1;
        }
        config.ApplyEnvironment(env.Env);
        if (config.GatewayUrl is null || config.ApiKey is null)
        {
            ui.Error("Not signed in to an Arena yet. Run: code-arena login");
            return 1;
        }
        using var http = Net.Client(Net.CaFile(o.Ca, config.Ca, env.Env));
        try
        {
            foreach (var m in await new GatewayClient(http, config.GatewayUrl, config.ApiKey).ModelsAsync(ct))
            {
                var traits = new List<string>();
                if (m.Context is { } c)
                {
                    traits.Add($"{c:N0} tokens");
                }
                if (m.Tools)
                {
                    traits.Add("tools");
                }
                if (m.Thinking)
                {
                    traits.Add("thinking");
                }
                ui.Line($"{(m.Id == config.Model ? "*" : " ")} {m.Id}  {ui.Dim(string.Join(" · ", traits))}");
            }
            return 0;
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException)
        {
            ui.Error(e.Message);
            return 1;
        }
    }

    /// <summary>
    /// The IDE (code-arena, code-arena web): this folder's files, terminals and
    /// agent behind a page on 127.0.0.1, until Ctrl+C. What the agent does is
    /// logged here as in a one-shot run (on stderr). A build without the page
    /// says so; without a command, it runs the conversation here instead.
    /// </summary>
    private static async Task<int> WebAsync(Options o, CliEnv env, CancellationToken ct)
    {
        var plain = o.NoColor || env.Env("NO_COLOR") is { Length: > 0 } || env.Env("TERM") == "dumb";
        var ui = new Ui(TextReader.Null, env.Out, env.Err, !plain && env.ErrTerminal, canAsk: false) { Quiet = true };
        var assets = env.Web ?? WebAssets.Embedded();
        if (!assets.Built)
        {
            if (o.Command is null)
            {
                ui.Warn("This code-arena has no IDE built into it: the conversation runs in this terminal.");
                return await AgentAsync(o, env, ct);
            }
            ui.Error("This code-arena has no IDE: its page was not built into it. tools/publish-code-arena.sh builds the page (from src/web) and then the program.");
            return 1;
        }
        // The whole run is one "turn" of Ctrl+C: the first press stops the server.
        using var key = env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        try
        {
            Runtime rt;
            try
            {
                rt = await Runtime.StartAsync(o, env, ui, linked.Token);
            }
            catch (Runtime.StartException e)
            {
                ui.Error(e.Message);
                return 1;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return 130;
            }
            await using (rt)
            {
                WebApp app;
                try
                {
                    app = WebApp.Start(rt, assets, o.Port ?? 0);
                }
                catch (System.Net.Sockets.SocketException e)
                {
                    ui.Error(e.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse
                        ? $"Port {o.Port} is taken: choose another with --port, or leave --port out for a free one."
                        : $"Could not listen on 127.0.0.1: {e.Message}");
                    return 1;
                }
                await using (app)
                {
                    var git = SystemPrompt.GitRoot(rt.Workspace.Root) is { } root && SystemPrompt.GitBranch(root) is { } branch ? $" ({branch})" : "";
                    env.Out.WriteLine($"Code Arena {Version}, the IDE for {rt.Workspace.Root}{git}: {rt.Model.Name}, mode {rt.Permissions.Mode.Name()}");
                    env.Out.WriteLine();
                    env.Out.WriteLine($"  {app.Address}");
                    env.Out.WriteLine();
                    env.Out.WriteLine("Only this machine can open it, and only with the key in the address. Ctrl+C stops it.");
                    env.Out.WriteLine("The agent in this terminal instead: code-arena chat");
                    if (!o.NoOpen && !Browser.Open(app.Address, env.Env))
                    {
                        env.Out.WriteLine("Open the address in your browser.");
                    }
                    env.Out.Flush();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, linked.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ctrl+C, or the caller stopped it.
                    }
                }
                ui.Info(rt.Agent.Messages.Count > 0 ? $"Stopped. The last session is saved as {rt.Session.Id}: code-arena --resume {rt.Session.Id}" : "Stopped.");
                return 0;
            }
        }
        finally
        {
            env.Cancel.EndTurn();
        }
    }

    private static async Task<int> AgentAsync(Options o, CliEnv env, CancellationToken ct)
    {
        var ui = MakeUi(env, o, quiet: o.Print);
        if (o.Print && (o.Prompt is null || o.Prompt == "-"))
        {
            o.Prompt = env.InTerminal ? null : await env.In.ReadToEndAsync(ct);
        }
        if (o.Print && string.IsNullOrWhiteSpace(o.Prompt))
        {
            ui.Error("-p needs a prompt: code-arena -p \"what to do\" (or pipe it in with -p -).");
            return 2;
        }
        if (o.Resume && o.ResumeId is null && !o.Print && ui.CanAsk)
        {
            o.ResumeId = PickSession(env, ui, env.Cwd);
            if (o.ResumeId is null)
            {
                o.Resume = false;
            }
        }
        Runtime rt;
        try
        {
            rt = await Runtime.StartAsync(o, env, ui, ct);
        }
        catch (Runtime.StartException e)
        {
            ui.Error(e.Message);
            return 1;
        }
        await using (rt)
        {
            return o.Print ? await OneShotAsync(rt, o.Prompt!, o.Output == "json", ct) : await new Repl(rt).RunAsync(o.Prompt, ct);
        }
    }

    /// <summary>Lists saved sessions (this folder's, else all) and asks which; null for none.</summary>
    internal static string? PickSession(CliEnv env, Ui ui, string cwd)
    {
        var sessions = SessionStore.List(env.Paths.SessionsDir, Path.GetFullPath(cwd), 15);
        if (sessions.Count == 0)
        {
            sessions = SessionStore.List(env.Paths.SessionsDir, null, 15);
        }
        if (sessions.Count == 0)
        {
            ui.Info("No saved sessions yet.");
            return null;
        }
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            ui.Line($"{i + 1,3}. {s.Updated:yyyy-MM-dd HH:mm}  {s.Preview}  {ui.Dim($"{s.Messages} messages · {s.Id}")}");
        }
        var answer = ui.ReadLine("Which one (number, Enter for none)? ")?.Trim();
        return int.TryParse(answer, out var n) && n >= 1 && n <= sessions.Count ? sessions[n - 1].Id : null;
    }

    private static async Task<int> OneShotAsync(Runtime rt, string prompt, bool json, CancellationToken ct)
    {
        var turn = new Spend();
        rt.Turn = turn;
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        string? answer = null;
        string? error = null;
        var code = 0;
        try
        {
            answer = await rt.Agent.RunAsync(prompt, turn, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            error = "Stopped.";
            code = 130;
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException)
        {
            error = e.Message;
            code = 1;
        }
        finally
        {
            rt.Env.Cancel.EndTurn();
        }
        if (json)
        {
            var result = new JsonObject
            {
                ["result"] = answer,
                ["is_error"] = error is not null,
                ["session"] = rt.Session.Id,
                ["model"] = rt.Model.Name,
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = turn.Prompt,
                    ["cached_tokens"] = turn.Cached,
                    ["completion_tokens"] = turn.Completion,
                    ["requests"] = turn.Requests,
                },
            };
            if (error is not null)
            {
                result["error"] = error;
            }
            rt.Env.Out.WriteLine(result.ToJsonString(Json.Relaxed));
        }
        else if (answer is not null)
        {
            rt.Env.Out.WriteLine(answer.Trim());
        }
        if (error is not null)
        {
            rt.Ui.Error(error);
        }
        return code;
    }
}
