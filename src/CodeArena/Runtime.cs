using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A session put together: the config, the gateway and its models, the tools (local and MCP), the agent.</summary>
internal sealed partial class Runtime : IAsyncDisposable
{
    private readonly List<McpClient> _servers = [];

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
    public Spend Total { get; } = new();
    /// <summary>The turn running now, so a sub-agent's tokens count in it.</summary>
    public Spend? Turn { get; set; }
    /// <summary>Whether Arena's MCP endpoint answered.</summary>
    public bool ArenaConnected { get; private set; }
    public IReadOnlyList<McpClient> Servers => _servers;

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
        await rt.InitAsync(o, ct);
        return rt;
    }

    private async Task InitAsync(Options o, CancellationToken ct)
    {
        Workspace = new Workspace(Env.Cwd, Config.AllowedPaths.Concat(o.AddDirs));
        var models = Gateway.ModelsAsync(ct);
        var arena = Config.ArenaTools && Config.ArenaMcpUrl is { } mcpUrl ? ConnectArenaAsync(mcpUrl, ct) : Task.FromResult<McpClient?>(null);
        var own = Config.McpServers.Where(s => !s.Disabled).Select(s => ConnectOwnAsync(s, ct)).ToList();
        try
        {
            Models = await models;
        }
        catch (Exception e) when (e is HttpRequestException or GatewayException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await Task.WhenAll(own.Cast<Task>().Append(arena));
            foreach (var t in own.Select(t => t.Result).Append(arena.Result).OfType<McpClient>())
            {
                await t.DisposeAsync();
            }
            throw new StartException(e.Message);
        }
        if (Models.Count == 0)
        {
            throw new StartException("The gateway lists no models for your key. Ask your admin which models you may use.");
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

        var arenaClient = await arena;
        var wanted = o.Model ?? resumed?.Model ?? Config.Model;
        var info = Models.FirstOrDefault(m => m.Id == wanted);
        if (info is null)
        {
            if (o.Model is not null)
            {
                throw new StartException($"The gateway has no model {o.Model}. Yours: {string.Join(", ", Models.Select(m => m.Id))}");
            }
            // No model of its own: the one a new chat in Arena starts with, as Arena MCP says, else the gateway's first.
            var fallback = Models.FirstOrDefault(m => m.Id == arenaClient?.DefaultModel) ?? Models[0];
            if (wanted is not null)
            {
                Ui.Warn($"{wanted} is not offered any more: using {fallback.Id}.");
            }
            info = fallback;
        }
        Model = new ModelState { Info = info, Thinking = o.Thinking ?? Config.Thinking, ContextOverride = Config.Context };

        var mode = Modes.Parse(o.Mode ?? Config.Mode);
        if ((o.Mode ?? Config.Mode) is { } m && mode is null)
        {
            throw new StartException($"There is no mode {m}. The modes: {string.Join(", ", Modes.Names)}.");
        }
        Permissions = new Permissions(Ui, mode ?? Mode.Ask);

        var tools = LocalTools.All(Config.Shell);
        tools.Add(LocalTools.SubAgentTool());
        var instructions = new List<(string, string)>();
        if (arenaClient is not null)
        {
            ArenaConnected = true;
            _servers.Add(arenaClient);
            foreach (var t in arenaClient.Tools)
            {
                var name = t.Str("name")!;
                // Arena decides who may call what; a tool it marks as changing things still asks here first.
                var changes = t["annotations"].Bool("destructiveHint") == true && t["annotations"].Bool("readOnlyHint") != true;
                tools.Add(Remote(arenaClient, t, tools.Any(x => x.Name == name) ? "arena_" + name : name, trusted: !changes, changesNothing: !changes));
            }
            if (arenaClient.Instructions is { } text)
            {
                instructions.Add(("Arena", text));
            }
            Permissions.Guard = LayaGuard.For(arenaClient, Workspace, Ui);
        }
        foreach (var task in own)
        {
            if (await task is not { } client)
            {
                continue;
            }
            _servers.Add(client);
            var server = Config.McpServers.First(s => s.Name == client.Name);
            foreach (var t in client.Tools)
            {
                tools.Add(Remote(client, t, OwnToolName(client.Name, t.Str("name")!), server.Trust, t["annotations"].Bool("readOnlyHint") == true));
            }
            if (client.Instructions is { } text)
            {
                instructions.Add((client.Name, text));
            }
        }
        Tools = new ToolBox(tools);

        var inputs = new SystemPrompt.Inputs
        {
            Workspace = Workspace,
            Paths = Env.Paths,
            Mode = () => Permissions.Mode,
            Model = () => Model.Name,
            Shell = Config.Shell,
            ArenaUrl = Config.Url,
            ServerInstructions = instructions,
            HasArenaTools = ArenaConnected,
        };
        Context = new ToolContext { Workspace = Workspace, Ui = Ui, Shell = Config.Shell };
        Agent = new Agent
        {
            Gateway = Gateway,
            Tools = Tools,
            Permissions = Permissions,
            Context = Context,
            Ui = Ui,
            Model = Model,
            SystemPrompt = () => SystemPrompt.Build(inputs),
            Total = Total,
            Stream = !Ui.Quiet,
        };
        Context.SubAgent = (description, prompt, token) => RunSubAgentAsync(inputs, description, prompt, token);
        if (Permissions.Guard is { } guard)
        {
            // Its one warning reaches the IDE's page too, as the agent's own do.
            guard.Notify = text => Agent.Events?.Notice(text);
        }

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
        };
        var report = await sub.RunAsync(prompt, Turn ?? new Spend(), ct);
        return report.Trim().Length > 0 ? report : $"The sub-agent ({description}) returned no report.";
    }

    private async Task<McpClient?> ConnectArenaAsync(string url, CancellationToken ct)
    {
        try
        {
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Config.ApiKey };
            return await McpClient.ConnectAsync("arena", new HttpMcpTransport(Http, url, headers), ct);
        }
        catch (McpUnavailableException)
        {
            Ui.Info($"Arena's tools are not available here (no MCP endpoint at {url}): carrying on with the local tools.");
        }
        catch (McpException e)
        {
            Ui.Warn($"Arena's tools did not connect: {e.Message} Carrying on with the local tools.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Ui.Warn($"Arena's tools did not answer in time ({url}): carrying on with the local tools.");
        }
        return null;
    }

    private async Task<McpClient?> ConnectOwnAsync(McpServerConfig server, CancellationToken ct)
    {
        try
        {
            IMcpTransport transport;
            if (server.Url is { Length: > 0 } url)
            {
                transport = new HttpMcpTransport(Http, url, server.Headers.ToDictionary(h => h.Key, h => StdioMcpTransport.Expand(h.Value)));
            }
            else if (server.Command is { Length: > 0 })
            {
                transport = StdioMcpTransport.Start(server, Workspace.Root);
            }
            else
            {
                Ui.Warn($"MCP server {server.Name} has neither a command nor a url: skipped.");
                return null;
            }
            return await McpClient.ConnectAsync(server.Name, transport, ct);
        }
        catch (McpException e)
        {
            Ui.Warn($"MCP server {server.Name} did not connect: {e.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Ui.Warn($"MCP server {server.Name} did not answer in time.");
        }
        return null;
    }

    /// <summary>An MCP tool as the model sees it, calling the server when run.</summary>
    public static ToolDef Remote(McpClient client, JsonObject tool, string name, bool trusted, bool changesNothing)
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
            Description = tool.Str("description") ?? tool.Str("title") ?? remoteName,
            Parameters = schema,
            Kind = ToolKind.Remote,
            Server = client.Name,
            Trusted = trusted,
            ChangesNothing = changesNothing,
            Summary = a => Fmt.OneLine(Json.Line(a), 100),
            Run = async (args, _, ct) =>
            {
                var result = await client.CallAsync(remoteName, args, ct);
                return new ToolResult(result.Text, result.IsError);
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
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
        Http.Dispose();
    }
}
