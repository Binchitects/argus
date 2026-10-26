using System.Diagnostics;
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
    /// <summary>A new session that carries on the plan an earlier session of this folder left in progress.</summary>
    public bool Plan { get; set; }
    public bool Resume { get; set; }
    public string? ResumeId { get; set; }
    /// <summary>A chat in Arena to continue here (its id, or its number in the list --web prints).</summary>
    public string? WebChat { get; set; }
    public string? Model { get; set; }
    public string? Mode { get; set; }
    public string? Thinking { get; set; }
    /// <summary>For this run: the share of the window (percent) at which the session compacts, and what it keeps.</summary>
    public int? CompactAt { get; set; }
    public int? CompactTarget { get; set; }
    public string? Ca { get; set; }
    public List<string> AddDirs { get; } = [];
    public string? Url { get; set; }
    public string? Gateway { get; set; }
    public bool NoColor { get; set; }
    /// <summary>The IDE: the port (null: a free one), and whether to leave the browser closed.</summary>
    public int? Port { get; set; }
    public bool NoOpen { get; set; }
    /// <summary>The IDE in a tab of the person's browser, not in a window of its own.</summary>
    public bool Browser { get; set; }
    /// <summary>The agent answers in the IDE's page (Markdown, links to files), not in this terminal.</summary>
    public bool Ide { get; set; }
    /// <summary>
    /// The IDE served for Arena's Code Studio (a workspace on the server): on every address of this machine, its key from
    /// CODE_ARENA_TOKEN, each request with it as a bearer token (Arena signs the person in and sends it); no window.
    /// </summary>
    public bool Serve { get; set; }
    /// <summary>web --connect: this folder in Arena's Code Studio, through a tunnel dialled with the person's API key.</summary>
    public bool Connect { get; set; }
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
    /// <summary>The program code-arena update replaces (null: this process's own).</summary>
    public string? Exe { get; init; }
    /// <summary>The terminal key by key, for the prompt's line editor (arrows, history); null: lines as they come.</summary>
    public IKeyboard? Keys { get; init; }
    public CancelKey Cancel { get; } = new();
    /// <summary>The web interface's page; null: the one built into the program.</summary>
    public WebAssets? Web { get; init; }
    /// <summary>Opens an address in the person's browser, false when there is none; null: the system's own way (Browser.Open).</summary>
    public Func<string, bool>? Browse { get; init; }
    /// <summary>Opens the IDE's own window on an address: a task that ends when it closes, null when it cannot (no browser for it); null: AppWindow.Open.</summary>
    public Func<string, Task?>? Window { get; init; }
    /// <summary>A window's browser that ends this soon handed it to one already running on the profile: its end is not the window's.</summary>
    public TimeSpan WindowHandOff { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>How long a browser opened for the IDE has to arrive before the person is told to open the address.</summary>
    public TimeSpan BrowserWait { get; init; } = TimeSpan.FromSeconds(20);
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
          code-arena update                 install the newer Code Arena your Arena serves, if any
          code-arena sandbox [command]      what the agent's commands may do here; with a command, run it as they run

        Options:
          -p, --print [prompt]       one-shot, for scripts; --output json for a JSON answer
          -c, --continue             carry on with this folder's last session
              --plan                 a new session that carries on the plan an earlier one left in progress here
          -r, --resume [id]          pick a saved session (or give its id)
              --web [N|id]           your chats in Arena; with N or an id, continue that one here
          -m, --model NAME           the model (see code-arena models)
              --mode MODE            ask (default), auto-edit, plan or yolo
              --thinking LEVEL       off, low, medium, high, xhigh, or default
              --compact-at PERCENT   compact the conversation at this share of the model's window (default: your
                                     Arena account's, else 80); off: only when a request would not fit
              --compact-to PERCENT   and keep the recent part within this share (default 25)
              --add-dir DIR          let the tools use another folder too (repeatable)
              --ca FILE              trust this CA certificate (PEM) besides the system's
              --url URL              login: the Arena's address, https://DOMAIN
              --gateway URL          login: the gateway, when it is not https://gateway.DOMAIN
              --no-color             plain text
              --port N               the IDE: listen on this port (default: a free one)
              --no-open              the IDE: print the address, do not open a window
              --browser              the IDE: in a tab of your browser, not a window of its own
              --serve                the IDE for Arena's Code Studio: on every address, its key from CODE_ARENA_TOKEN
              --connect              the IDE: this folder in Arena's Code Studio too (needs code-arena login)
          -v, --version              the version, the licence and where the source is
          -h, --help

        In the terminal, /help lists the commands. Ctrl+C stops a turn; twice at the prompt leaves.
        Settings: code-arena keeps them in config.json in its config folder (see docs/code-arena.md).
        """;

    public static async Task<int> RunAsync(string[] args, CliEnv env, CancellationToken ct = default)
    {
        if (args is ["sandbox", .. var rest])
        {
            return await SandboxAsync(rest, env, ct);
        }
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
            case "update":
                return await UpdateAsync(o, env, MakeUi(env, o, quiet: false), ct);
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
            if (i == 0 && a is "login" or "logout" or "models" or "update" or "web" or "chat" or "help" or "version")
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
                case "--plan":
                    o.Plan = true;
                    break;
                case "-r" or "--resume":
                    o.Resume = true;
                    if (NextIsValue() && SessionId().IsMatch(args[i + 1]))
                    {
                        o.ResumeId = args[++i];
                    }
                    break;
                case "--web":
                    o.WebChat = NextIsValue() ? args[++i] : "";
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
                case "--compact-at":
                    o.CompactAt = Compaction.Percent(Value()) ?? throw new ArgumentException("--compact-at is a share of the model's window: 70, 70% or 0.7, or off.");
                    break;
                case "--compact-to":
                    o.CompactTarget = Compaction.Percent(Value()) ?? throw new ArgumentException("--compact-to is a share of the model's window: 30, 30% or 0.3.");
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
                case "--browser":
                    o.Browser = true;
                    break;
                case "--serve":
                    o.Serve = true;
                    o.NoOpen = true;
                    break;
                case "--connect":
                    o.Connect = true;
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
            if (config.SyncChats && await ChatSync.WhoAsync(check, http, ct) is { } account)
            {
                // A key of another account (a test one) would keep the sessions with that account's chats, not the person's.
                ui.Line($"{ui.Green("✓")} Sessions are kept with {account}'s chats in Arena: the key's account.");
            }
            var argus = new Config { Url = url, ArgusUrl = config.ArgusUrl };
            if (config.ArgusTools && argus.ArgusMcpUrl is { } argusUrl)
            {
                try
                {
                    await using var client = await McpClient.ConnectAsync("argus", new HttpMcpTransport(http, argusUrl,
                        new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }), ct);
                    ui.Line($"{ui.Green("✓")} Argus's tools: {client.Tools.Count} ({string.Join(", ", client.Tools.Take(6).Select(t => t.Str("name")))}{(client.Tools.Count > 6 ? ", …" : "")}).");
                }
                catch (McpUnavailableException)
                {
                    ui.Info($"Argus's tools are not available on this Arena (no MCP endpoint at {argusUrl}).");
                }
                catch (Exception e) when (e is McpException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    ui.Warn($"Argus's tools did not connect: {(e is OperationCanceledException ? $"{argusUrl} did not answer in time." : e.Message)} Sessions try again in the background.");
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

    /// <summary>code-arena update: Arena's newer build of Code Arena for this system, in this program's place.</summary>
    private static async Task<int> UpdateAsync(Options o, CliEnv env, Ui ui, CancellationToken ct)
    {
        if (LoadConfig(env, ui) is not { } config)
        {
            return 1;
        }
        config.ApplyEnvironment(env.Env);
        using var http = Net.Client(Net.CaFile(o.Ca, config.Ca, env.Env));
        var (done, said) = await Updates.UpdateAsync(config, http, ui.Info, ct, env.Exe);
        if (done)
        {
            ui.Line(said);
            return 0;
        }
        ui.Error(said);
        return 1;
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
    /// Served for Code Studio (--serve), or connected to it (--connect), it needs no page: Arena shows its own.
    /// </summary>
    private static async Task<int> WebAsync(Options o, CliEnv env, CancellationToken ct)
    {
        var plain = o.NoColor || env.Env("NO_COLOR") is { Length: > 0 } || env.Env("TERM") == "dumb";
        var ui = new Ui(TextReader.Null, env.Out, env.Err, !plain && env.ErrTerminal, canAsk: false) { Quiet = true };
        var assets = env.Web ?? WebAssets.Embedded();
        if (!assets.Built && !o.Serve && !o.Connect)
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
            o.Ide = true;
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
                    // Served for Code Studio: the key Arena's workspace service gave it, at least 32 characters.
                    var served = o.Serve ? env.Env("CODE_ARENA_TOKEN") is { Length: >= 32 } given ? given : throw new ArgumentException("--serve needs CODE_ARENA_TOKEN, at least 32 characters.") : null;
                    app = WebApp.Start(rt, assets, o.Port ?? 0, served);
                }
                catch (ArgumentException e)
                {
                    ui.Error(e.Message);
                    return 1;
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
                    env.Out.WriteLine(o.Serve ? $"  served on port {app.Port} for Code Studio" : $"  {app.Address}");
                    env.Out.WriteLine();
                    env.Out.WriteLine(o.Serve ? "Only with its key, as a bearer token. Ctrl+C stops it." : "Only this machine can open it, and only with the key in the address. Ctrl+C stops it.");
                    env.Out.WriteLine("The agent in this terminal instead: code-arena chat");
                    // This folder in Code Studio too: a tunnel to Arena, with the person's key, for as long as this runs.
                    if (o.Connect)
                    {
                        if (MachineFor(rt, env, app) is { } tunnel)
                        {
                            _ = tunnel.RunAsync(linked.Token);
                        }
                        else
                        {
                            ui.Warn("--connect needs Arena: sign in with code-arena login first. The IDE runs here meanwhile.");
                        }
                    }
                    using var launcher = o.NoOpen ? null : Launcher.Write(env.Paths.DataDir, app.Port, app.Address);
                    // Its own window first (config.json "window": false, or --browser, a tab instead), else a tab.
                    var window = launcher is null || o.Browser || !rt.Config.Window ? null
                        : (env.Window ?? (url => AppWindow.Open(url, Path.Combine(env.Paths.DataDir, "window"), env.Env)))(launcher.Url);
                    var closed = Task.Delay(Timeout.Infinite, linked.Token);
                    if (window is not null)
                    {
                        _ = NoBrowserYetAsync(app.Opened, env.BrowserWait, env.Out, linked.Token);
                        closed = WindowClosedAsync(window, env.WindowHandOff, linked.Token);
                        env.Out.WriteLine("Closing its window stops it too.");
                    }
                    else if (launcher is not null && (env.Browse ?? (url => Browser.Open(url, env.Env)))(launcher.Url))
                    {
                        _ = NoBrowserYetAsync(app.Opened, env.BrowserWait, env.Out, linked.Token);
                    }
                    else if (!o.NoOpen)
                    {
                        env.Out.WriteLine("Open the address in your browser.");
                    }
                    env.Out.Flush();
                    try
                    {
                        await closed;
                        ui.Info("Its window was closed.");
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

    /// <summary>
    /// The tunnel that puts this folder in Arena's Code Studio: Arena's address and the person's key (code-arena login),
    /// the folder's id there (kept with the folder's state, so it is the same workspace each time), its name (the folder
    /// on this machine), its project (origin's path, when GitLab's) and branch; null without Arena.
    /// </summary>
    private static MachineTunnel? MachineFor(Runtime rt, CliEnv env, WebApp app)
    {
        if (rt.Config.Url is not { Length: > 0 } url || !Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var arena) || rt.Config.ApiKey is not { Length: > 0 } key)
        {
            return null;
        }
        var state = new WorkspaceState(env.Paths.DataDir, rt.Workspace.Root);
        var id = state.Read().Str("studioMachine") is { Length: 32 } kept ? kept : Guid.NewGuid().ToString("N");
        state.Merge(new System.Text.Json.Nodes.JsonObject { ["studioMachine"] = id });
        var root = SystemPrompt.GitRoot(rt.Workspace.Root);
        var name = $"{Path.GetFileName(rt.Workspace.Root.TrimEnd(Path.DirectorySeparatorChar))} on {Environment.MachineName}";
        return new MachineTunnel(arena, key, app.Port, app.Token, name[..Math.Min(200, name.Length)], root is null ? null : OriginProject(root),
            root is null ? null : SystemPrompt.GitBranch(root), id, said =>
            {
                env.Out.WriteLine(said);
                env.Out.Flush();
            });
    }

    /// <summary>origin's project path (group/project) from its address (https or ssh), when it has one.</summary>
    internal static string? OriginProject(string root)
    {
        try
        {
            var config = File.ReadAllText(Path.Combine(root, ".git", "config"));
            var m = System.Text.RegularExpressions.Regex.Match(config, @"\[remote ""origin""\][^\[]*?url\s*=\s*(\S+)");
            if (!m.Success)
            {
                return null;
            }
            var address = m.Groups[1].Value;
            var path = address.Contains("://", StringComparison.Ordinal) ? new Uri(address).AbsolutePath : address[(address.IndexOf(':') + 1)..];
            path = path.Trim('/');
            return path.EndsWith(".git", StringComparison.Ordinal) ? path[..^4] : path;
        }
        catch (Exception e) when (e is IOException or UriFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// When no browser has the page (its cookie with it) 20 s after one was
    /// opened: says to open the address by hand. A browser in a sandbox (a
    /// snap, as Ubuntu's Firefox is, or a Flatpak) cannot read the launcher in
    /// its hidden folder.
    /// </summary>
    private static async Task NoBrowserYetAsync(Task opened, TimeSpan wait, TextWriter output, CancellationToken ct)
    {
        try
        {
            await opened.WaitAsync(wait, ct);
        }
        catch (TimeoutException)
        {
            output.WriteLine("No browser has opened the IDE yet: open the address above in yours. (A browser in a sandbox, as a snap or a Flatpak, cannot read the file code-arena opened it with.)");
            output.Flush();
        }
        catch (OperationCanceledException)
        {
            // Stopped first.
        }
    }

    /// <summary>
    /// Ends when the IDE's window closes: its browser ended. One that ends at once handed the window to a browser
    /// already running on the profile (another code-arena's), so its end says nothing: Ctrl+C stops this one.
    /// </summary>
    private static async Task WindowClosedAsync(Task window, TimeSpan handOff, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await window.WaitAsync(ct);
        if (Stopwatch.GetElapsedTime(started) < handOff)
        {
            await Task.Delay(Timeout.Infinite, ct);
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

    /// <summary>code-arena sandbox [command]: the sandbox the agent's commands get in this folder, and a command run in it.</summary>
    private static async Task<int> SandboxAsync(string[] command, CliEnv env, CancellationToken ct)
    {
        Config config;
        try
        {
            config = Config.Load(env.Paths.ConfigFile);
        }
        catch (InvalidOperationException e)
        {
            env.Err.WriteLine($"code-arena: {e.Message}");
            return 1;
        }
        Sandbox sandbox;
        try
        {
            sandbox = Sandbox.Choose(config, new Workspace(env.Cwd, config.AllowedPaths), env.Paths, env.Env, $"check-{Environment.ProcessId}");
        }
        catch (Runtime.StartException e)
        {
            env.Err.WriteLine($"code-arena: {e.Message}");
            return 1;
        }
        env.Out.WriteLine(sandbox.Describe());
        if (command.Length == 0)
        {
            return 0;
        }
        try
        {
            var ui = new Ui(TextReader.Null, env.Out, env.Err, false, false);
            var context = new ToolContext { Workspace = new Workspace(env.Cwd, config.AllowedPaths), Ui = ui, Shell = config.Shell, Sandbox = sandbox };
            var result = await Proc.RunAsync(LocalTools.ShellCommand(string.Join(' ', command), context), TimeSpan.FromMinutes(10), ct);
            env.Out.Write(result.Output);
            return result.ExitCode;
        }
        finally
        {
            sandbox.Clean();
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
            // Nobody types a next message here.
            answer = await rt.FinishJobsAsync(turn, linked.Token) ?? answer;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // What it was waiting for stops with it.
            foreach (var job in rt.Jobs.Waiting)
            {
                job.Stop("by the person");
            }
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

/// <summary>
/// What the browser is opened with: a page in a file of the person's own (0600,
/// in a folder of theirs alone, 0700) that sends it on to the address. The
/// address holds the run's key, and a program's command line is open to every
/// user of the machine (ps, /proc/PID/cmdline): only the file's name goes on
/// the browser's, as Jupyter does it. The file goes when the run ends.
/// </summary>
internal sealed class Launcher : IDisposable
{
    private Launcher(string path) => Path = path;

    public string Path { get; }

    /// <summary>The file as a file:// address: not a path, which a system could run as a program.</summary>
    public string Url => new Uri(Path).AbsoluteUri;

    /// <summary>The launcher for this port in the data folder's "open"; null when it cannot be written there (the address is still printed).</summary>
    public static Launcher? Write(string dataDir, int port, string address)
    {
        var dir = System.IO.Path.Combine(dataDir, "open");
        var file = System.IO.Path.Combine(dir, $"code-arena-{port}.html");
        var html = System.Net.WebUtility.HtmlEncode(address);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(dir); // the person's own profile: only theirs to read
            }
            else
            {
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                // Made before, or by an older version: theirs alone now, and not a link to elsewhere.
                if (new DirectoryInfo(dir).LinkTarget is not null)
                {
                    return null;
                }
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            // One left by a run that did not end well, on the same port.
            File.Delete(file);
            using (var stream = new FileStream(file, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write($"""
                    <!doctype html><html lang="en"><head><meta charset="utf-8"><meta http-equiv="refresh" content="0;url={html}"><meta name="referrer" content="no-referrer"><title>Code Arena</title></head>
                    <body style="font: 15px/1.5 system-ui, sans-serif; max-width: 36rem; margin: 15vh auto; padding: 0 1rem"><p>Opening Code Arena. If it does not open, <a href="{html}">open it here</a>.</p></body></html>
                    """);
            }
            return new Launcher(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gone already; or kept, holding a key that ends with this run.
        }
    }
}
