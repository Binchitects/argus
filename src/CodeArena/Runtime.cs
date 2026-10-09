using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A session put together: the config, the gateway and its models, the tools (local and MCP), the agent.</summary>
internal sealed partial class Runtime : IAsyncDisposable
{
    /// <summary>How long a session waits, as it starts, for its MCP servers' first answers: the rest join in the background.</summary>
    public static TimeSpan StartWait { get; set; } = TimeSpan.FromSeconds(3);

    private readonly List<ServerLink> _links = [];
    private readonly object _serversGate = new();
    private IReadOnlyList<(string Server, string Text)> _instructions = [];

    private Runtime(Config config, Ui ui, CliEnv env, HttpClient http, GatewayClient gateway)
    {
        Config = config;
        Ui = ui;
        Env = env;
        Http = http;
        Gateway = gateway;
    }

    public Config Config { get; }
    public Ui Ui { get; }
    public CliEnv Env { get; }
    public HttpClient Http { get; }
    public GatewayClient Gateway { get; }
    public List<ModelInfo> Models { get; private set; } = [];
    public ModelState Model { get; private set; } = null!;
    public Permissions Permissions { get; private set; } = null!;
    public ToolBox Tools { get; private set; } = null!;
    public ToolContext Context { get; private set; } = null!;
    public Agent Agent { get; private set; } = null!;
    public Workspace Workspace { get; private set; } = null!;
    public SessionStore Session { get; private set; } = null!;
    /// <summary>What was sent in this folder, in the terminal and the IDE's chat alike.</summary>
    public InputHistory History { get; private set; } = null!;
    public Spend Total { get; } = new();
    /// <summary>The turn running now, so a sub-agent's tokens count in it.</summary>
    public Spend? Turn { get; set; }
    /// <summary>The commands running with no time limit, and those that ended.</summary>
    public CommandJobs Jobs { get; } = new();
    /// <summary>When the session compacts itself.</summary>
    public Compaction Compaction { get; } = new();
    /// <summary>The session's MCP servers: Arena's, Argus's and the person's own, each connected in the background.</summary>
    public IReadOnlyList<ServerLink> Links => _links;
    /// <summary>Whether Arena's MCP endpoint answered.</summary>
    public bool ArenaConnected => _links.Any(l => l.Name == ArenaName && l.State == LinkState.Connected);
    /// <summary>Told when a server connects or is lost (on a background thread): the terminal says so at its next prompt, the IDE's page reads it.</summary>
    public Action<ServerLink>? ServersChanged { get; set; }

    public const string ArenaName = "arena";
    public const string ArgusName = "argus";

    /// <summary>Thrown for what stops a session before it starts, with what to do about it.</summary>
    public sealed class StartException(string message) : Exception(message);

    public static async Task<Runtime> StartAsync(Options o, CliEnv env, Ui ui, CancellationToken ct)
    {
        Config config;
        try
        {
            config = Config.Load(env.Paths.ConfigFile);
        }
        catch (InvalidOperationException e)
        {
            throw new StartException(e.Message);
        }
        config.ApplyEnvironment(env.Env);
        if (config.GatewayUrl is null || string.IsNullOrEmpty(config.ApiKey))
        {
            throw new StartException("Not signed in to an Arena yet. Run: code-arena login");
        }
        // Said now, not found later as a server that never connects.
        if (config.WrongAddress(env.Paths.ConfigFile) is { } wrong)
        {
            throw new StartException(wrong);
        }
        HttpClient http;
        try
        {
            http = Net.Client(Net.CaFile(o.Ca, config.Ca, env.Env));
        }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException)
        {
            throw new StartException(e.Message);
        }
        var rt = new Runtime(config, ui, env, http, new GatewayClient(http, config.GatewayUrl, config.ApiKey));
        try
        {
            await rt.InitAsync(o, ct);
        }
        catch
        {
            await rt.DisposeAsync();
            throw;
        }
        return rt;
    }

    private async Task InitAsync(Options o, CancellationToken ct)
    {
        Workspace = new Workspace(Env.Cwd, Config.AllowedPaths.Concat(o.AddDirs));
        History = new InputHistory(Env.Paths.HistoryDir, Workspace.Root);
        var mode = Modes.Parse(o.Mode ?? Config.Mode);
        if ((o.Mode ?? Config.Mode) is { } m && mode is null)
        {
            throw new StartException($"There is no mode {m}. The modes: {string.Join(", ", Modes.Names)}.");
        }
        if (Compaction.Set(o.CompactAt ?? Config.CompactAt, o.CompactTarget ?? Config.CompactTarget) is { } wrong)
        {
            throw new StartException($"Compaction: {wrong} (--compact-at, --compact-to, or compactAt and compactTarget in {Env.Paths.ConfigFile})");
        }
        Permissions = new Permissions(Ui, mode ?? Mode.Ask);
        // The terminal watches the commands with no time limit: their output as it comes, and how they ended.
        var printer = new JobPrinter(Ui);
        Jobs.Started += printer.Started;
        Jobs.Output += printer.Output;
        Jobs.Ended += printer.Ended;
        var tools = LocalTools.All(Config.Shell);
        tools.Add(LocalTools.SubAgentTool());
        Tools = new ToolBox(tools);

        // The servers connect in the background; the session starts when the gateway has said which models there are.
        var models = Gateway.ModelsAsync(ct);
        AddLinks();
        var arena = _links.FirstOrDefault(l => l.Name == ArenaName);
        // A command waits for Arena's first answer, so Laya looks at it whenever Arena offers decide.
        Permissions.GuardReady = arena?.FirstTry ?? Task.CompletedTask;
        foreach (var link in _links)
        {
            link.Start();
        }
        try
        {
            Models = await models;
        }
        catch (Exception e) when (e is HttpRequestException or GatewayException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new StartException(e.Message);
        }
        if (Models.Count == 0)
        {
            throw new StartException("The gateway lists no models for your key. Ask your admin which models you may use.");
        }
        // A few seconds more for the servers (a run that cannot wait for them later, -p, gives them their whole handshake).
        var firstTries = Task.WhenAll(_links.Select(l => l.FirstTry));
        await Task.WhenAny(firstTries, Task.Delay(o.Print ? TimeSpan.FromSeconds(25) : StartWait, ct));
        foreach (var link in _links.Where(l => l.State == LinkState.Connecting))
        {
            Ui.Info($"{link.Title}'s tools are still connecting: they join the session when they answer.");
        }

        // A resumed session keeps its history, model and to-do list.
        SessionData? resumed = null;
        string? resumedFile = null;
        if (o.Resume || o.Continue)
        {
            resumedFile = o.ResumeId is { } id ? SessionStore.Find(Env.Paths.SessionsDir, id)
                : SessionStore.List(Env.Paths.SessionsDir, Workspace.Root, 1).FirstOrDefault()?.File;
            if (resumedFile is null)
            {
                if (o.ResumeId is not null)
                {
                    throw new StartException($"No saved session {o.ResumeId}. code-arena chat --resume lists them.");
                }
                Ui.Info("No earlier session in this folder: starting a new one.");
            }
            else
            {
                resumed = SessionStore.Load(resumedFile);
            }
        }

        var wanted = o.Model ?? resumed?.Model ?? Config.Model;
        var info = Models.FirstOrDefault(x => x.Id == wanted);
        if (info is null)
        {
            if (o.Model is not null)
            {
                throw new StartException($"The gateway has no model {o.Model}. Yours: {string.Join(", ", Models.Select(x => x.Id))}");
            }
            // No model of its own: the one a new chat in Arena starts with, as Arena MCP says, else the gateway's first.
            var fallback = Models.FirstOrDefault(x => x.Id == arena?.Client?.DefaultModel) ?? Models[0];
            if (wanted is not null)
            {
                Ui.Warn($"{wanted} is not offered any more: using {fallback.Id}.");
            }
            info = fallback;
        }
        Model = new ModelState { Info = info, Thinking = o.Thinking ?? Config.Thinking, ContextOverride = Config.Context };

        var inputs = new SystemPrompt.Inputs
        {
            Workspace = Workspace,
            Paths = Env.Paths,
            Mode = () => Permissions.Mode,
            Model = () => Model.Name,
            Shell = Config.Shell,
            ArenaUrl = Config.Url,
            ServerInstructions = () => Volatile.Read(ref _instructions),
            HasArenaTools = () => ArenaConnected,
        };
        Context = new ToolContext { Workspace = Workspace, Ui = Ui, Shell = Config.Shell, Jobs = Jobs };
        // Whatever the agent commits is by Code Arena (its author), the person staying the committer.
        var harness = CommitIdentity.From(Config);
        Proc.Author = harness;
        Agent = new Agent
        {
            CommitAs = Config.AutoCommit ? harness : null,
            Gateway = Gateway,
            Tools = Tools,
            Permissions = Permissions,
            Context = Context,
            Ui = Ui,
            Model = Model,
            SystemPrompt = () => SystemPrompt.Build(inputs),
            Total = Total,
            Stream = !Ui.Quiet,
            Compaction = Compaction,
        };
        Context.SubAgent = (description, prompt, token) => RunSubAgentAsync(inputs, description, prompt, token);

        if (resumed is not null && resumedFile is not null)
        {
            Session = SessionStore.Open(resumedFile);
            Agent.Load(resumed.Messages);
            Context.Todos = resumed.Todos;
            Total.Prompt = resumed.Prompt;
            Total.Cached = resumed.Cached;
            Total.Completion = resumed.Completion;
            if (resumed.Model != Model.Name)
            {
                Session.Model(Model.Name);
            }
        }
        else
        {
            Session = SessionStore.Create(Env.Paths.SessionsDir, Workspace.Root, Model.Name);
        }
        Agent.Session = Session;
        Context.TodosChanged = todos => Session.Todos(todos);
    }

    /// <summary>
    /// The session's servers, each in the background: Arena's MCP endpoint and Argus's (with the
    /// person's key, unless the config turns them off), then the person's own.
    /// </summary>
    private void AddLinks()
    {
        var auth = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Config.ApiKey };
        if (Config.ArenaTools && Config.ArenaMcpUrl is { } arenaUrl)
        {
            Link(new ServerLink(ArenaName, "Arena", arenaUrl, ct => McpClient.ConnectAsync(ArenaName, new HttpMcpTransport(Http, arenaUrl, auth), ct)));
        }
        if (Config.ArgusTools && Config.ArgusMcpUrl is { } argusUrl)
        {
            Link(new ServerLink(ArgusName, "Argus", argusUrl, async ct =>
            {
                try
                {
                    return await McpClient.ConnectAsync(ArgusName, new HttpMcpTransport(Http, argusUrl, auth), ct);
                }
                catch (McpException e) when (e.NoSuchHost && Config.ArgusUrl is not { Length: > 0 })
                {
                    // Found by its name beside the Arena's (argus.DOMAIN): an Arena without Argus has no such name. Not an error.
                    throw new McpUnavailableException($"{argusUrl} does not resolve: this Arena has no Argus at argus.DOMAIN (\"argusUrl\" in config.json gives its address).");
                }
            }));
        }
        foreach (var server in Config.McpServers.Where(s => !s.Disabled))
        {
            if (server.Url is not { Length: > 0 } && server.Command is not { Length: > 0 })
            {
                Ui.Warn($"MCP server {server.Name} has neither a command nor a url: skipped.");
                continue;
            }
            if (server.Url is { Length: > 0 } address && !Config.IsWebAddress(address))
            {
                Ui.Warn($"MCP server {server.Name}: {address} is not an http or https address: skipped.");
                continue;
            }
            Link(new ServerLink(server.Name, server.Name, server.Url ?? server.Command, ct =>
            {
                IMcpTransport transport = server.Url is { Length: > 0 } url
                    ? new HttpMcpTransport(Http, url, server.Headers.ToDictionary(h => h.Key, h => StdioMcpTransport.Expand(h.Value)))
                    : StdioMcpTransport.Start(server, Workspace.Root);
                return McpClient.ConnectAsync(server.Name, transport, ct);
            }));
        }
    }

    private void Link(ServerLink link)
    {
        link.Changed = OnServerChanged;
        _links.Add(link);
    }

    /// <summary>A server connected (its tools and instructions join), or was lost (they go); said in the terminal once per change.</summary>
    private void OnServerChanged(ServerLink link, McpClient? client, McpClient? gone)
    {
        lock (_serversGate)
        {
            var (remote, notes) = Served();
            Tools.Update(all => all.Where(t => t.Server is null).Concat(remote));
            Volatile.Write(ref _instructions, notes);
            if (link.Name == ArenaName)
            {
                Permissions.Guard = LayaGuard.For(client, Workspace, Ui);
                if (Permissions.Guard is { } guard)
                {
                    // Its one warning reaches the IDE's page too, as the agent's own do.
                    guard.Notify = said => Agent?.Events?.Notice(said);
                }
            }
        }
        Said(link, client, gone);
        ServersChanged?.Invoke(link);
    }

    /// <summary>
    /// The servers' tools and instructions, from those connected now. Arena's: renamed arena_… where
    /// a local tool has the name. Argus's: those Arena serves already (its Argus tools) are left to
    /// Arena, and Argus's instructions with them; when Arena is not connected, Argus's own are used.
    /// The person's own: mcp__server__tool.
    /// </summary>
    private (List<ToolDef> Tools, List<(string Server, string Text)> Notes) Served()
    {
        var local = Tools.All.Where(t => t.Server is null).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var tools = new List<ToolDef>();
        var notes = new List<(string Server, string Text)>();
        foreach (var link in _links.OrderBy(l => l.Name == ArenaName ? 0 : 1))
        {
            if (link.Client is not { } client)
            {
                continue;
            }
            var own = link.Name is ArenaName or ArgusName ? null : Config.McpServers.FirstOrDefault(s => s.Name == link.Name);
            var added = 0;
            foreach (var t in client.Tools)
            {
                var name = t.Str("name")!;
                if (own is not null)
                {
                    tools.Add(Remote(client, t, OwnToolName(client.Name, name), own.Trust, t["annotations"].Bool("readOnlyHint") == true, link));
                }
                else if (link.Name == ArgusName && tools.Any(x => x.Server == ArenaName && x.RemoteName == name))
                {
                    continue;
                }
                else
                {
                    // Arena (and Argus) decide who may call what; a tool marked as changing things still asks here first.
                    var changes = t["annotations"].Bool("destructiveHint") == true && t["annotations"].Bool("readOnlyHint") != true;
                    tools.Add(Remote(client, t, local.Contains(name) ? $"{link.Name}_{name}" : name, trusted: !changes, changesNothing: !changes, link));
                }
                added++;
            }
            if (client.Instructions is { Length: > 0 } text && (added > 0 || client.Tools.Count == 0))
            {
                notes.Add((link.Title, text));
            }
        }
        return (tools, notes);
    }

    /// <summary>
    /// What the terminal says of a change: when a server connects after the session started, and
    /// once when it fails (again when it was connected and is lost), not at every try.
    /// </summary>
    private void Said(ServerLink link, McpClient? client, McpClient? gone)
    {
        lock (_said)
        {
            if (client is not null)
            {
                _said.Remove(link.Name);
                if (Agent is not null)
                {
                    Notice(false, $"{link.Title}'s tools connected: {Tools.All.Count(t => t.Server == link.Name)}.");
                }
                return;
            }
            if (!_said.Add(link.Name) && gone is null)
            {
                return;
            }
        }
        var again = link.NextTry is { } next ? $" Tried again at {next.ToLocalTime():HH:mm:ss} (/mcp retry tries now)." : "";
        if (link.State == LinkState.Unavailable)
        {
            Notice(false, link.Name switch
            {
                ArenaName => $"Arena's tools are not available here (no MCP endpoint at {link.Url}): carrying on with the local tools.",
                ArgusName => $"Argus's tools are not available here: {link.Error}",
                _ => $"MCP server {link.Name} has no MCP endpoint at {link.Url}.",
            });
            return;
        }
        Notice(true, link.Name switch
        {
            ArenaName => $"Arena's tools did not connect: {link.Error} Carrying on with the local tools meanwhile.{again}",
            ArgusName => $"Argus's tools did not connect: {link.Error}{again}",
            _ => $"MCP server {link.Name} did not connect: {link.Error}{again}",
        });
    }

    // The servers whose failure was said, until they connect.
    private readonly HashSet<string> _said = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<(bool Warn, string Text)> _later = new();

    /// <summary>The terminal is waiting for the person to type: what servers say waits for the next line (<see cref="SayLater"/>).</summary>
    public bool AtPrompt { get; set; }

    private void Notice(bool warn, string text)
    {
        if (AtPrompt)
        {
            _later.Enqueue((warn, text));
            return;
        }
        // Not into a question waiting for its answer, nor the middle of the model's line.
        Ui.Background(warn ? Ui.Yellow("! " + text) : Ui.Dim(text));
    }

    /// <summary>What the servers said while the terminal waited at its prompt.</summary>
    public void SayLater()
    {
        while (_later.TryDequeue(out var said))
        {
            if (said.Warn)
            {
                Ui.Warn(said.Text);
            }
            else
            {
                Ui.Info(said.Text);
            }
        }
    }

    /// <summary>A new, empty session (/clear): the same tools and model.</summary>
    public void NewSession()
    {
        Session = SessionStore.Create(Env.Paths.SessionsDir, Workspace.Root, Model.Name);
        Agent.Session = Session;
        Agent.Clear();
        Context.Todos = [];
        Context.TodosChanged = todos => Session.Todos(todos);
    }

    /// <summary>Switches to a saved session (/resume).</summary>
    public void Resume(string file)
    {
        var data = SessionStore.Load(file);
        Session = SessionStore.Open(file);
        Agent.Session = Session;
        Agent.Load(data.Messages);
        Context.Todos = data.Todos;
        Context.TodosChanged = todos => Session.Todos(todos);
        if (data.Model is not null && data.Model != Model.Name && Models.FirstOrDefault(m => m.Id == data.Model) is { } info)
        {
            Model.Info = info;
        }
    }

    public void SwitchModel(ModelInfo info)
    {
        Model.Info = info;
        Session.Model(info.Id);
    }

    /// <summary>
    /// Sets when the session compacts (percent of the window; null keeps the current) and keeps it in
    /// config.json for the next sessions; null when done, else why not.
    /// </summary>
    public string? SetCompaction(int? at, int? target)
    {
        if (Compaction.Set(at, target) is { } wrong)
        {
            return wrong;
        }
        try
        {
            // The file as it is (not with this run's environment overrides), with the two values changed.
            var file = Config.Load(Env.Paths.ConfigFile);
            file.CompactAt = Compaction.At == Compaction.DefaultAt ? null : Compaction.At;
            file.CompactTarget = Compaction.Target == Compaction.DefaultTarget ? null : Compaction.Target;
            file.Save(Env.Paths.ConfigFile);
            Config.CompactAt = file.CompactAt;
            Config.CompactTarget = file.CompactTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Warn($"Set for this session, but not saved: {e.Message}");
        }
        return null;
    }

    /// <summary>"Arena: 14 tools", "Argus: its tools come through Arena", "Argus: not connected (…)".</summary>
    public string Describe(ServerLink link)
    {
        if (link.State == LinkState.Connected)
        {
            var count = Tools.All.Count(t => t.Server == link.Name);
            return count == 0 && link.Client?.Tools.Count > 0
                ? $"{link.Title}: connected, its tools come through Arena"
                : $"{link.Title}: {count} tool{(count == 1 ? "" : "s")}";
        }
        return link.Describe() + (link.State == LinkState.Failed && link.Error is { } error ? $" ({Fmt.OneLine(error, 120)})" : "");
    }

    private async Task<string> RunSubAgentAsync(SystemPrompt.Inputs inputs, string description, string prompt, CancellationToken ct)
    {
        var sub = new Agent
        {
            Gateway = Gateway,
            Tools = Tools.ForSubAgent(),
            Permissions = Permissions,
            Context = new ToolContext { Workspace = Workspace, Ui = Ui, Shell = Config.Shell },
            Ui = Ui,
            Model = Model,
            SystemPrompt = () => SystemPrompt.Build(inputs, subAgent: true),
            Total = Total,
            Depth = 1,
            Stream = false,
            MaxSteps = 60,
            Compaction = Compaction,
        };
        var report = await sub.RunAsync(prompt, Turn ?? new Spend(), ct);
        return report.Trim().Length > 0 ? report : $"The sub-agent ({description}) returned no report.";
    }

    /// <summary>An MCP tool as the model sees it, calling the server when run; a call that finds the server gone has it connect again.</summary>
    public static ToolDef Remote(McpClient client, JsonObject tool, string name, bool trusted, bool changesNothing, ServerLink? link = null)
    {
        var remoteName = tool.Str("name")!;
        var schema = tool["inputSchema"] is JsonObject s ? s.Clone() : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (schema.Str("type") is null)
        {
            schema["type"] = "object";
        }
        return new ToolDef
        {
            Name = name,
            RemoteName = remoteName,
            Description = tool.Str("description") ?? tool.Str("title") ?? remoteName,
            Parameters = schema,
            Kind = ToolKind.Remote,
            Server = client.Name,
            Trusted = trusted,
            ChangesNothing = changesNothing,
            Summary = a => Fmt.OneLine(Json.Line(a), 100),
            Run = async (args, _, ct) =>
            {
                try
                {
                    var result = await client.CallAsync(remoteName, args, ct);
                    return new ToolResult(result.Text, result.IsError);
                }
                catch (McpException e) when (e.Lost && link is not null)
                {
                    link.Lost(e.Message);
                    throw;
                }
            },
        };
    }

    /// <summary>mcp__server__tool, as Claude Code names them: letters, digits, _ and - only, at most 64 characters.</summary>
    public static string OwnToolName(string server, string tool)
    {
        var name = $"mcp__{Unsafe().Replace(server, "_")}__{Unsafe().Replace(tool, "_")}";
        return name.Length <= 64 ? name : name[..64];
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();

    public async ValueTask DisposeAsync()
    {
        Jobs.StopAll("code-arena stopped");
        foreach (var link in _links)
        {
            await link.DisposeAsync();
        }
        Http.Dispose();
    }
}
